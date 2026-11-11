using System.Net;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>
/// An approved provider, ready to be called: the only thing a protocol client may send a request for. Made only by
/// <see cref="ChatProviderRepository.ResolveTargetAsync"/>, after the sealed approval has been opened and found to name exactly this
/// protocol, base URL and local flag. Carries no key.
/// </summary>
public sealed class ProviderEgressTarget
{
    internal ProviderEgressTarget(Guid providerId, string name, string protocol, Uri baseUri, bool isLocal, ProviderWireOptions options)
    {
        ProviderId = providerId;
        Name = name;
        Protocol = protocol;
        BaseUri = baseUri;
        IsLocal = isLocal;
        Options = options;
    }

    public Guid ProviderId { get; }
    public string Name { get; }
    public string Protocol { get; }
    /// <summary>The approved base URL, without a trailing slash.</summary>
    public Uri BaseUri { get; }
    public bool IsLocal { get; }
    public ProviderWireOptions Options { get; }

    /// <summary>The approved host (IDN host, lower case) and port: the only place a request for this target may go.</summary>
    public string Host => BaseUri.IdnHost.ToLowerInvariant();
    public int Port => BaseUri.Port;

    /// <summary><c>{base}/{relative}</c>: the request URI of an endpoint of this provider.</summary>
    public Uri Endpoint(string relative) => new(BaseUri.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'));

    /// <summary>A target for a unit test of a protocol client (the egress still checks every request against it).</summary>
    internal static ProviderEgressTarget ForTest(string baseUrl, bool isLocal = false, ProviderWireOptions? options = null, string name = "test",
        string protocol = ProviderProtocols.OpenAiChat) =>
        new(Guid.NewGuid(), name, protocol, ProviderUrl.Parse(baseUrl, isLocal), isLocal, options ?? ProviderWireOptions.Default);
}

/// <summary>Nothing was sent: the provider is not approved, or a request did not match its approval, or the address it resolved to is one this
/// provider may not use. Not a key's fault and not transient: the next model of a fallback chain is tried, the keys are left alone.</summary>
public sealed class ProviderUnavailableException(string message) : Exception(message);

/// <summary>
/// The one way out to a model provider, for every protocol client (OpenAI format, Anthropic Messages, Gemini). It holds the rules the
/// provider's approval stands for, and refuses before anything is sent:
/// <list type="bullet">
/// <item>HTTPS only; plain HTTP only for a provider marked local.</item>
/// <item>The request goes to the approved host and port, never anywhere else (a key is only ever put on a request for its own provider's target,
/// see <see cref="ModelRouter"/>, so a key reaches only its own provider's host).</item>
/// <item>No redirect is followed: a 3xx is an answer like any other error (a redirect would re-send the conversation to wherever it points).</item>
/// <item>For a provider NOT marked local the socket is opened only to a public address, judged where the socket is opened on the address the
/// name resolved to (<see cref="PeerSocketConnector"/> with <see cref="HostAddressPolicy.IsPublic"/>), so DNS rebinding cannot move it onto
/// loopback, a private network or a metadata service. A local provider may use loopback and private addresses, never link-local or a cloud
/// metadata address (<see cref="PeerNetworkRules"/>).</item>
/// <item>No system proxy: a proxy would resolve and connect on the node's behalf and the address check would judge the proxy.</item>
/// </list>
/// </summary>
public sealed class ProviderEgress(IHttpClientFactory clients)
{
    /// <summary>The named client of providers on the open internet: public addresses only.</summary>
    public const string PublicClientName = "model-provider-public";

    /// <summary>The named client of providers marked local (Ollama, LM Studio ...): loopback and private addresses allowed.</summary>
    public const string LocalClientName = "model-provider-local";

    /// <summary>Checks <paramref name="request"/> against <paramref name="target"/>, then sends it through the client of the target's kind.</summary>
    /// <exception cref="ProviderUnavailableException">The request does not match the approval, or the connection was refused by the address
    /// rule. Nothing reached the provider.</exception>
    public async Task<HttpResponseMessage> SendAsync(ProviderEgressTarget target, HttpRequestMessage request, HttpCompletionOption completion,
        CancellationToken ct)
    {
        Check(target, request.RequestUri);
        var client = clients.CreateClient(target.IsLocal ? LocalClientName : PublicClientName);
        try
        {
            return await client.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex) when (PeerSocketConnector.IsRefusal(ex))
        {
            throw new ProviderUnavailableException(target.IsLocal
                ? $"The address of provider '{target.Name}' resolves to a place this node does not call (link-local or a cloud metadata service)."
                : $"The address of provider '{target.Name}' resolves to a non-public address. Mark the provider as local if it runs on your own network.");
        }
    }

    /// <summary>The request rules of an approved target (see the class remarks). Public for the tests of each rule.</summary>
    public static void Check(ProviderEgressTarget target, Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            throw new ProviderUnavailableException("A provider request must have an absolute address.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && target.IsLocal))
            throw new ProviderUnavailableException($"Provider '{target.Name}' may only be called over HTTPS (plain HTTP is for a provider marked local).");
        if (uri.Scheme != target.BaseUri.Scheme)
            throw new ProviderUnavailableException($"A request for provider '{target.Name}' does not use the approved scheme.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ProviderUnavailableException($"A request for provider '{target.Name}' carries user information in its address.");
        if (!string.Equals(uri.IdnHost, target.BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != target.Port)
            throw new ProviderUnavailableException($"A request for provider '{target.Name}' is not addressed to its approved host.");
    }

    /// <summary>The handler of <see cref="PublicClientName"/>: no redirect, no proxy, sockets only to public addresses.</summary>
    public static SocketsHttpHandler PublicHandler(PeerNetwork? network = null)
    {
        var net = network ?? PublicNetwork;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, ct) => PeerSocketConnector.ConnectAsync(context, net, ct),
        };
    }

    /// <summary>The handler of <see cref="LocalClientName"/>: no redirect, no proxy, never link-local or a metadata address.</summary>
    public static SocketsHttpHandler LocalHandler(PeerNetwork? network = null)
    {
        var net = network ?? PeerNetwork.Production;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, ct) => PeerSocketConnector.ConnectAsync(context, net, ct),
        };
    }

    /// <summary>The open internet only (<see cref="HostAddressPolicy.IsPublic"/>), resolved by the system.</summary>
    public static readonly PeerNetwork PublicNetwork = new(HostAddressPolicy.IsPublic, (host, ct) => Dns.GetHostAddressesAsync(host, ct));
}

/// <summary>
/// The base URL of a provider: parsed, checked and written one way. Absolute, https (or http for a local provider), no user information, no
/// query and no fragment, no trailing slash. A provider on the open internet may not be named by an address literal that is not public or by
/// <c>localhost</c> (the connect step judges a name again on what it resolves to); no provider may be a cloud metadata service.
/// </summary>
public static class ProviderUrl
{
    public const int MaxLength = 2048;

    /// <summary>The normalized base URL, or the reason it is refused.</summary>
    public static (string? Url, string? Error) Normalize(string? raw, bool isLocal)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "The base URL is required.");
        raw = raw.Trim();
        if (raw.Length > MaxLength) return (null, $"The base URL is longer than {MaxLength} characters.");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return (null, "The base URL is not an absolute address (for example https://api.example.com/v1).");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return (null, "The base URL must start with https:// (or http:// for a provider on this computer or your own network).");
        if (uri.Scheme == Uri.UriSchemeHttp && !isLocal)
            return (null, "Plain http:// is only allowed for a provider marked local. Use https://, or mark the provider as local.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return (null, "The base URL must not carry a user name or password.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return (null, "The base URL must not have a query (?) or a fragment (#).");
        var host = uri.IdnHost.Trim('[', ']');
        if (host.Length == 0) return (null, "The base URL has no host.");
        if (PeerNetworkRules.HostRefusal(host) is { } refusal) return (null, refusal);
        if (!isLocal)
        {
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return (null, "localhost is this computer: mark the provider as local to use it.");
            if (IPAddress.TryParse(host, out var ip) && !HostAddressPolicy.IsPublic(ip))
                return (null, "That address is not on the public internet: mark the provider as local to use it.");
        }
        return (Canonical(uri), null);
    }

    /// <summary>A base URL already normalized (from the database); throws when it is not one.</summary>
    public static Uri Parse(string url, bool isLocal)
    {
        var (normalized, error) = Normalize(url, isLocal);
        if (normalized is null) throw new ProviderUnavailableException(error!);
        return new Uri(normalized);
    }

    /// <summary>scheme://host[:port]/path, lower-case scheme and IDN host, the default port left out, no trailing slash.</summary>
    public static string Canonical(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}{path}";
    }

    /// <summary>host[:port] of a normalized base URL, as the approval names it to a person.</summary>
    public static string DisplayHost(string normalizedUrl)
    {
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)) return normalizedUrl;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? host : host + ":" + uri.Port;
    }
}
