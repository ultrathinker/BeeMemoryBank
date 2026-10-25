using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// The address the person's browser used to open this page, for the places that print one back to them (the AI-assistant snippet on the
/// Profile page, the address of a blind-node invitation).
/// <para>
/// In the desktop app, the Windows service and the Mac app the browser does not talk to this process: it talks to the node's front, which
/// relays the request with YARP's default headers and addresses this process by its OWN loopback port. So <c>Request.Host</c> here is that
/// internal port, an address nothing outside the node can use: printed in the snippet it made Claude Code report 404 on <c>/mcp</c>, because
/// the Web process has no <c>/mcp</c>. The authority the browser used travels in <c>X-Forwarded-Host</c>, which the forwarded-headers
/// middleware does not consume (it honours only <c>For</c> and <c>Proto</c>).
/// </para>
/// <para>
/// The header is read only when the node says it runs behind its front (<c>BMB_BEHIND_LOOPBACK_PROXY</c>, set by the node on its Web child
/// and by nothing else), and then its LAST value: the front appends the authority it saw to whatever the client sent, so the nearest hop is
/// the one worth believing. A value that is not a plain host[:port] is ignored.
/// </para>
/// </summary>
public static class BrowserAuthority
{
    public const string BehindNodeFrontSetting = "BMB_BEHIND_LOOPBACK_PROXY";

    private const string ForwardedHostHeader = "X-Forwarded-Host";

    /// <summary>True when this process runs behind the node's front (desktop, Windows service, Mac app).</summary>
    public static bool IsBehindNodeFront(HttpRequest request) =>
        !string.IsNullOrEmpty(request.HttpContext.RequestServices?.GetService<IConfiguration>()?[BehindNodeFrontSetting]);

    /// <summary>host[:port] the browser used: the last valid <c>X-Forwarded-Host</c> behind the node's front, else <c>Request.Host</c>.</summary>
    public static HostString Of(HttpRequest request, bool behindNodeFront)
    {
        if (behindNodeFront && request.Headers.TryGetValue(ForwardedHostHeader, out var values))
        {
            var last = values
                .SelectMany(v => (v ?? "").Split(','))
                .Select(v => v.Trim())
                .LastOrDefault(v => v.Length > 0);
            if (last is not null && IsPlainAuthority(last)) return new HostString(last);
        }
        return request.Host;
    }

    /// <summary><c>scheme://host[:port]</c> the browser used. The scheme follows <c>X-Forwarded-Proto</c> when a proxy left it on the request.</summary>
    public static string Origin(HttpRequest request, bool behindNodeFront)
    {
        var forwarded = request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var scheme = forwarded is "http" or "https" ? forwarded : request.Scheme;
        return $"{scheme}://{Of(request, behindNodeFront)}";
    }

    /// <summary>Same as <see cref="Origin(HttpRequest, bool)"/>, asking the request's own services whether the node is behind its front.</summary>
    public static string Origin(HttpRequest request) => Origin(request, IsBehindNodeFront(request));

    /// <summary>host or host:port, where host is a DNS name, an IPv4 address or a bracketed IPv6 address and port is 1..65535. Nothing else.</summary>
    private static bool IsPlainAuthority(string value)
    {
        if (value.Length is 0 or > 255) return false;
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or '?' or '#' or '"' or '\'' or '`' or '$' or '<' or '>'))
            return false;

        string host;
        string? port = null;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']');
            if (close < 0) return false;
            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length != 0)
            {
                if (rest[0] != ':') return false;
                port = rest[1..];
            }
            if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon >= 0)
            {
                // A second colon is an IPv6 address without brackets (or garbage): the port could not be told from the address.
                if (value.IndexOf(':', colon + 1) >= 0) return false;
                host = value[..colon];
                port = value[(colon + 1)..];
            }
            else
            {
                host = value;
            }
            if (host.Length == 0 || Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.IPv6) return false;
        }

        return port is null || (port.Length is >= 1 and <= 5 && port.All(char.IsAsciiDigit) && int.Parse(port) is >= 1 and <= 65535);
    }
}
