using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The address of a peer answers, but nothing there speaks sync (BMB-307): the identity page is missing (404, 405, 410), the page moved (a 3xx; the
/// sync clients do not follow redirects to places they were not pinned to), or what came back is not an identity (a web page, a login form, the JSON of
/// another service). It is not "the peer did not answer" (<see cref="HttpRequestException"/> with no status) and not "the peer's door is shut" (401, 403):
/// the person has to correct the address, and every screen says so in the same words (<see cref="Headline"/>).
/// <para>
/// The text never carries the address (BMB-307 fix round): a stored or typed address may hold a user name or a token, and this message reaches dialogs, API
/// answers and logs. The screen that asked knows which address it asked about.
/// </para>
/// </summary>
public sealed class NoSyncServerException(string reason)
    : InvalidOperationException($"No sync server at this address: {reason}. Check the address of this device.")
{
    /// <summary>The words every screen starts with.</summary>
    public const string Headline = "no sync server at this address";

    /// <summary>What was answered, without the address (the status pages keep addresses out of what they show).</summary>
    public string Reason { get; } = reason;
}

/// <summary>What <c>/api/sync/identity</c> of a peer says about itself (a 2.5 peer sends no capabilities and no version).</summary>
public sealed record RemoteIdentity(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion = 0,
    IReadOnlyList<string>? Capabilities = null, string? AppVersion = null);

public enum AddressVerdict
{
    /// <summary>A sync server answers with its identity.</summary>
    SyncServer,

    /// <summary>Something answers, but it is not a sync server (<see cref="NoSyncServerException"/>).</summary>
    NoSyncServer,

    /// <summary>Nothing answered in a way that says what is there: no connection, a timeout, a shut door (401, 403), a server in trouble (5xx).</summary>
    NoAnswer,
}

public sealed record AddressCheck(AddressVerdict Verdict, RemoteIdentity? Identity, string Message);

/// <summary>The public key a peer states about itself, against the one this node has on record (BMB-307 fix round: the node id alone is only a claim).</summary>
public static class PeerKey
{
    /// <summary>True when <paramref name="declaredB64"/> is the stored key. Constant time; a declared key that is not base64 does not match.</summary>
    public static bool Matches(byte[]? stored, string? declaredB64)
    {
        if (stored is not { Length: > 0 } || string.IsNullOrWhiteSpace(declaredB64)) return false;
        try { return CryptographicOperations.FixedTimeEquals(stored, Convert.FromBase64String(declaredB64)); }
        catch (FormatException) { return false; }
    }
}

/// <summary>One peer a sync could not finish with, and why in a sentence a person can act on (no address, key or token in it).</summary>
public sealed record PeerSyncFailure(Guid NodeId, string Name, string Reason)
{
    public override string ToString() => $"{Name}: {Reason}";

    public static PeerSyncFailure From(WhitelistEntry peer, Exception ex) =>
        new(peer.NodeId, string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.NodeId.ToString()[..8] : peer.DisplayName, PeerContactLog.Describe(ex));
}

public static class SyncAddress
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The most an identity page may be: it is a few hundred bytes of JSON, so anything near this is not one.</summary>
    public const int MaxIdentityBytes = 64 * 1024;

    /// <summary>How long the body of the identity page may take once its headers are in (the client's own timeout covers the headers).</summary>
    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads the identity page of the peer at <paramref name="baseUrl"/>. Throws <see cref="NoSyncServerException"/> when the answer says nothing
    /// there speaks sync (a missing page, a redirect, a page that is not an identity, a body far bigger than an identity); every other failure is what
    /// the HTTP client threw (<see cref="HttpRequestException"/> with its status, a timeout), because a shut door or a server in trouble is a server.
    /// <para>
    /// The answer is read as a bounded stream: the headers first (a declared length over <see cref="MaxIdentityBytes"/> ends it), then at most that many
    /// bytes within <paramref name="bodyTimeout"/>, so that an address that answers with an endless or a trickling body costs this node neither memory nor
    /// a hung screen. A body that does not arrive in time is "no answer", not "no sync server".
    /// </para>
    /// </summary>
    public static async Task<RemoteIdentity> ReadIdentityAsync(HttpClient http, string baseUrl, CancellationToken ct = default, TimeSpan? bodyTimeout = null)
    {
        baseUrl = baseUrl.TrimEnd('/');
        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync($"{baseUrl}/api/sync/identity", HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } redirected && (int)redirected is >= 300 and < 400)
        {
            // The pinned handler (SpkiPinGuardHandler) never follows a redirect and says so by throwing: the same finding as a 3xx answered to a plain client.
            throw new NoSyncServerException($"it answered {StatusText((int)ex.StatusCode!.Value)}");
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Gone || status is >= 300 and < 400)
                throw new NoSyncServerException($"it answered {StatusText(status)}");
            resp.EnsureSuccessStatusCode();

            if (resp.Content.Headers.ContentLength is > MaxIdentityBytes)
                throw new NoSyncServerException("it answered with far more than a sync identity (a web page, or another service)");
            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) && !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                throw new NoSyncServerException("it answered, but not with a sync identity (a web page, or another service)");

            var body = await ReadBoundedAsync(resp, bodyTimeout ?? BodyTimeout, ct);
            RemoteIdentity? identity = null;
            try { identity = JsonSerializer.Deserialize<RemoteIdentity>(body, Json); }
            catch (JsonException) { }
            if (identity is null || identity.NodeId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Ed25519PublicKeyB64))
                throw new NoSyncServerException("it answered, but not with a sync identity (a web page, or another service)");
            return identity;
        }
    }

    /// <summary>
    /// The status, with a fixed name for the few that matter here. NEVER the reason phrase of the answer: it is whatever the remote host (or a proxy in front of
    /// it) wrote, and this text reaches screens, API answers and logs.
    /// </summary>
    internal static string StatusText(int status) => status switch
    {
        404 => "404 Not Found",
        405 => "405 Method Not Allowed",
        410 => "410 Gone",
        301 => "301 Moved Permanently",
        302 => "302 Found",
        303 => "303 See Other",
        307 => "307 Temporary Redirect",
        308 => "308 Permanent Redirect",
        _ => status.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(limit.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (body.Length + read > MaxIdentityBytes)
                    throw new NoSyncServerException("it answered with far more than a sync identity (a web page, or another service)");
                body.Write(buffer, 0, read);
            }
            return body.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("The peer did not send its identity in time.", new TimeoutException());
        }
    }

    /// <summary>What is at <paramref name="baseUrl"/>: for a screen that checks an address before it is saved.</summary>
    public static async Task<AddressCheck> CheckAsync(HttpClient http, string baseUrl, CancellationToken ct = default, TimeSpan? bodyTimeout = null)
    {
        try
        {
            var identity = await ReadIdentityAsync(http, baseUrl, ct, bodyTimeout);
            return new AddressCheck(AddressVerdict.SyncServer, identity, "A sync server answers at this address.");
        }
        catch (NoSyncServerException ex)
        {
            return new AddressCheck(AddressVerdict.NoSyncServer, null, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AddressCheck(AddressVerdict.NoAnswer, null, PeerContactLog.Describe(ex));
        }
    }
}
