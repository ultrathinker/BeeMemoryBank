using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services;

/// <summary>What the probe found at a hub's address.</summary>
/// <param name="PresentedPin">The pin of the key the node presented (pin mode only).</param>
/// <param name="PubliclyTrusted">Whether the certificate it presented also validates through the system's chain (pin mode only).</param>
public sealed record HubProbeResult(Guid NodeId, string Ed25519PublicKeyB64, string? PresentedPin, bool PubliclyTrusted);

/// <summary>The probe could not vouch for the address; the message is written for the administrator and carries no secret.</summary>
public sealed class HubProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where the probe may connect: which addresses are allowed and how a name is resolved. Production is
/// <see cref="Production"/>; a test that needs a hub on the loopback interface passes its own, so the rule itself is never
/// switched off in a shipped build.
/// </summary>
public sealed record HubProbeNetwork(Func<IPAddress, bool> IsAllowed, Func<string, CancellationToken, Task<IPAddress[]>> Resolve)
{
    public static readonly HubProbeNetwork Production =
        new(HostAddressPolicy.IsPossibleHub, (host, ct) => Dns.GetHostAddressesAsync(host, ct));
}

/// <summary>
/// The check behind "Let blind copies call this node" (ADR 0007): connect to the address the administrator typed the way a
/// blind copy will, and learn what is there — which node answers and, by the trust mode, whether its certificate is good.
///
/// <para><c>public-ca</c>: the TLS handshake must validate through the system's chain, name included
/// (<see cref="PublicCaTls"/>); nothing else passes. <c>pin</c>: the node's key is not vouched for by anyone, so the
/// handshake reads the key it presents and the caller pins it — trust on first use, which is why the answer carries the
/// pin for the administrator to compare, and why an expected pin, when given, must match. Nothing secret crosses this
/// connection in either mode: one GET of the public identity document. It never follows a redirect and never falls back
/// to http (the caller has already required an https origin).</para>
///
/// <para>The address is typed by an administrator and this server connects to it, so it must not become a way to probe this
/// machine or its neighbours: a blind copy can only call a hub through an address it can reach, so loopback, link-local (the
/// cloud metadata address included), unspecified and multicast targets are refused
/// (<see cref="HostAddressPolicy.IsPossibleHub"/>); private LAN ranges stay allowed. The check is made where the socket is
/// opened: on every address the name resolves to, and again on the address actually connected to, so a name cannot pass on
/// one answer and connect on another. No system proxy is used, because a proxy would resolve and connect on this server's
/// behalf and the check would judge the proxy instead of the target.</para>
/// </summary>
public sealed class HubTrustProbe(TlsTrustAnchors anchors, HubProbeNetwork? network = null)
{
    private readonly HubProbeNetwork _network = network ?? HubProbeNetwork.Production;
    private const int MaxIdentityBytes = 64 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Identity(Guid NodeId, string? Ed25519PublicKeyB64);

    public async Task<HubProbeResult> ProbeAsync(string origin, string trust, string? expectedPin, CancellationToken ct)
    {
        var authority = new Uri(origin).Authority;
        var errors = SslPolicyErrors.None;
        string? presented = null;
        var publiclyTrusted = false;
        var pinRefused = false;

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = Timeout,
            ConnectCallback = ConnectCheckedAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, sslErrors) =>
                {
                    errors = sslErrors;
                    if (certificate is null) return false;
                    if (trust == BlindTrust.PublicCa) return PublicCaTls.IsValid(certificate, chain, sslErrors, anchors);

                    using var presentedCertificate = new X509Certificate2(certificate);
                    presented = SpkiPin.Of(presentedCertificate);
                    publiclyTrusted = PublicCaTls.IsValid(certificate, chain, sslErrors, anchors);
                    pinRefused = expectedPin is not null && !SpkiPin.Matches(certificate, expectedPin);
                    return !pinRefused;
                }
            }
        };
        using var http = new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = MaxIdentityBytes };

        Identity? identity;
        try
        {
            using var response = await http.GetAsync($"{origin}/api/sync/identity", ct);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HubProbeException($"{authority} redirects to another address. Give the final https address, without a redirect.");
            if (!response.IsSuccessStatusCode)
                throw new HubProbeException($"{authority} did not answer the identity request as a Bee Memory Bank node does. Is this a Bee Memory Bank node, and is /api/sync/ forwarded to it?");
            identity = await response.Content.ReadFromJsonAsync<Identity>(Json, ct);
        }
        catch (HttpRequestException ex) when (Find<AddressRefusedException>(ex) is not null)
        {
            throw new HubProbeException(
                $"{authority} is at an address that can never be a hub (this machine itself, a link-local or cloud-metadata address, " +
                "an unspecified or a multicast address). Give the address a blind copy will use to reach it: a public one, or one on your own network.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || errors != SslPolicyErrors.None || pinRefused)
        {
            throw new HubProbeException(Explain(authority, trust, errors, pinRefused), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HubProbeException(Unreachable(authority, ex), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HubProbeException($"{authority} did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw new HubProbeException($"{authority} did not answer with a node identity. Is this a Bee Memory Bank node?", ex);
        }

        if (identity is null || identity.NodeId == Guid.Empty || string.IsNullOrEmpty(identity.Ed25519PublicKeyB64))
            throw new HubProbeException($"{authority} did not answer with a node identity.");
        return new HubProbeResult(identity.NodeId, identity.Ed25519PublicKeyB64, presented, publiclyTrusted);
    }

    private sealed class AddressRefusedException : Exception;

    /// <summary>
    /// Opens the socket for the handler: resolves the host itself, refuses if ANY address it resolves to is not a possible hub,
    /// connects only to the addresses it has just checked, and checks the address it ended up connected to.
    /// </summary>
    private async ValueTask<Stream> ConnectCheckedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _network.Resolve(host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !_network.IsAllowed(a))) throw new AddressRefusedException();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            if (socket.RemoteEndPoint is not IPEndPoint remote || !_network.IsAllowed(remote.Address)) throw new AddressRefusedException();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var depth = 0; ex is not null && depth < 8; ex = ex.InnerException, depth++)
            if (ex is T found) return found;
        return null;
    }

    /// <summary>Why the node could not be reached, in words that come from this server's own network stack and not from the target.</summary>
    private static string Unreachable(string authority, HttpRequestException ex) => Find<SocketException>(ex)?.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"The name {authority} does not resolve from this server.",
        SocketError.ConnectionRefused => $"{authority} refused the connection.",
        SocketError.TimedOut => $"{authority} did not answer in time.",
        _ => $"Could not reach {authority}."
    };

    private static string Explain(string authority, string trust, SslPolicyErrors errors, bool pinRefused)
    {
        if (pinRefused)
            return $"The node at {authority} presents another key than the pin you gave. Check the address and the pin.";
        if (trust != BlindTrust.PublicCa)
            return $"Could not make a secure connection to {authority}.";
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return $"The certificate at {authority} was issued for another name than the address you gave. Use the name the certificate is for.";
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return $"{authority} presented no certificate.";
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return $"The certificate at {authority} is not trusted by this server: it is expired, self-signed, or issued by an authority this server does not know. " +
                   "Fix the certificate, or choose \"Pinned certificate\" if this node uses a certificate of its own.";
        return $"Could not make a secure connection to {authority}.";
    }
}
