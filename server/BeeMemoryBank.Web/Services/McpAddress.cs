namespace BeeMemoryBank.Web.Services;

/// <summary>Where the MCP address on the Profile page came from. Nothing but tests and docs read this.</summary>
public enum McpAddressSource
{
    /// <summary>No address a client could use is known.</summary>
    None,

    /// <summary><c>BMB_MCP_URL</c>, as the operator set it.</summary>
    Configured,

    /// <summary>The browse address, because the page is served by the node's front door, which also serves <c>/mcp</c>.</summary>
    NodeFront,

    /// <summary>The browse address, because the page was opened through a reverse proxy that forwards <c>/mcp</c>.</summary>
    ReverseProxy,

    /// <summary><c>http://127.0.0.1:&lt;port&gt;/mcp</c>, because the API port is published on the host's loopback.</summary>
    PublishedLoopbackPort,
}

/// <summary>
/// The MCP address to print in the connection snippet for an AI assistant, or the reason there is none.
/// <see cref="Url"/> is null exactly when <see cref="Source"/> is <see cref="McpAddressSource.None"/>;
/// <see cref="Message"/> then says why, in words the person looking at the page can act on.
/// </summary>
public sealed record McpAddress(string? Url, McpAddressSource Source, string? Message)
{
    public bool IsAvailable => Url is not null;
}

/// <summary>
/// Works out which address an AI assistant can really use to reach <c>/mcp</c>. The Web page is not
/// where <c>/mcp</c> lives: the API serves it (port 5300 in Docker and from source), and only the
/// desktop/service/Mac front door puts it on the same address as the page. So the address the user
/// browses from is a good answer in those cases and a dead one in plain Docker, where the API port
/// is not even published. Nothing here reads the environment; <see cref="McpAddressInputs"/> is the
/// whole input, which keeps every rule below testable.
/// </summary>
public static class McpAddressResolver
{
    public const string UnavailableMessage =
        "AI assistants cannot reach this node yet: its AI interface (/mcp) is not published. " +
        "With Docker, start it with docker-compose.reverse-proxy.yml, or switch on the optional API port " +
        "lines in docker-compose.yml. See docs/deployment.md.";

    public const string InvalidConfiguredMessage =
        "AI assistants cannot reach this node yet: BMB_MCP_URL is set but is not a usable address. " +
        "It must start with http:// or https:// and carry no user name, query or fragment, " +
        "for example https://bee.example.com/mcp. See docs/deployment.md.";

    /// <summary>First match wins; see docs/deployment.md ("AI assistants and the MCP address").</summary>
    public static McpAddress Resolve(McpAddressInputs input)
    {
        if (!string.IsNullOrWhiteSpace(input.ConfiguredUrl))
        {
            // The operator named an address. Whatever is wrong with it is theirs to fix; guessing
            // another one here would print an address they did not choose.
            return TryNormalizeConfigured(input.ConfiguredUrl, out var configured)
                ? new McpAddress(configured, McpAddressSource.Configured, null)
                : new McpAddress(null, McpAddressSource.None, InvalidConfiguredMessage);
        }

        if (input.BehindNodeFront && TryBrowseOrigin(input.BrowseOrigin, out var frontUrl))
            return new McpAddress(frontUrl, McpAddressSource.NodeFront, null);

        if (input.ViaReverseProxy && TryBrowseOrigin(input.BrowseOrigin, out var proxyUrl))
            return new McpAddress(proxyUrl, McpAddressSource.ReverseProxy, null);

        if (TryParsePort(input.PublishedApiPort, out var port))
            return new McpAddress($"http://127.0.0.1:{port}/mcp", McpAddressSource.PublishedLoopbackPort, null);

        return new McpAddress(null, McpAddressSource.None, UnavailableMessage);
    }

    /// <summary>
    /// <c>https://bee.example.com</c> becomes <c>https://bee.example.com/mcp</c>; an address that already
    /// has a path keeps it (a proxy may serve MCP under a prefix), minus a trailing slash. Anything that is
    /// not an absolute http(s) address, or carries credentials, a query or a fragment, is refused: the
    /// value ends up inside a snippet next to an agent key, so it must not be able to smuggle one more
    /// argument or a token into a command line the user will paste.
    /// </summary>
    internal static bool TryNormalizeConfigured(string value, out string url)
    {
        url = "";
        var trimmed = value.Trim();
        if (!IsSnippetSafe(trimmed)) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        url = uri.GetLeftPart(UriPartial.Authority) + (path.Length == 0 ? "/mcp" : path);
        return true;
    }

    private static bool TryBrowseOrigin(string origin, out string url)
    {
        url = "";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        url = uri.GetLeftPart(UriPartial.Authority) + "/mcp";
        return IsSnippetSafe(url);
    }

    private static bool IsSnippetSafe(string value) =>
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '\\' or '`' or '$');

    private static bool TryParsePort(string? value, out int port) =>
        int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65535;
}

/// <param name="ConfiguredUrl"><c>BMB_MCP_URL</c>; null or blank when unset.</param>
/// <param name="BehindNodeFront">The Web process runs behind the node's front door (desktop, Windows service, Mac app).</param>
/// <param name="ViaReverseProxy">This request carried forwarding headers, so a reverse proxy is in front of the page.</param>
/// <param name="PublishedApiPort"><c>BMB_API_PUBLISHED_PORT</c>: the host port the API is published on, on loopback; null when it is not.</param>
/// <param name="BrowseOrigin">Scheme and authority the user browses from, e.g. <c>https://bee.example.com</c>.</param>
public sealed record McpAddressInputs(
    string? ConfiguredUrl,
    bool BehindNodeFront,
    bool ViaReverseProxy,
    string? PublishedApiPort,
    string BrowseOrigin);
