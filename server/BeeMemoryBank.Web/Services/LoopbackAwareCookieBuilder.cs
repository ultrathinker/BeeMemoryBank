using System.Net;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// The session cookie is <c>Secure</c> everywhere, with one exception: a plain-HTTP request addressed to the machine itself
/// (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) that did not come through a proxy and whose connection itself comes from this machine (the
/// headers below are claims any peer can make; the peer address is not).
/// <para>
/// The desktop app shows the web UI in an embedded browser at <c>http://127.0.0.1:5310</c>. Chromium (Edge WebView2, Chrome) accepts a
/// <c>Secure</c> cookie from such an address, but WebKit (the macOS app's WKWebView, Safari) drops it: the sign-in succeeded on the server,
/// the next request carried no cookie and the user was sent back to the login page in an endless loop. A cookie that never leaves the
/// loopback interface gains nothing from the <c>Secure</c> flag, while anything that reaches the node through a network address or a proxy
/// still gets it.
/// </para>
/// <para>
/// In the desktop app the browser does not talk to this process: it talks to the node's front on :5310, which relays every request here
/// with YARP's default headers. <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are consumed by the forwarded-headers middleware (it
/// turns them into the client address and <c>IsHttps</c> and removes them), but <c>X-Forwarded-Host</c> is not honoured there and stays on
/// the request: it is the authority the browser itself used, and <c>Request.Host</c> is only this process's own loopback address. So when
/// that header is present the browser's authority is read from it (every value of it must be a loopback name), otherwise from
/// <c>Request.Host</c>. A header the middleware left over means another proxy stands in front of the front and the cookie stays
/// <c>Secure</c>: a TLS-terminating proxy sets <c>X-Forwarded-Proto: https</c>, which the front appends to rather than replaces.
/// </para>
/// Because it overrides <c>Build</c>, the relaxed flag also applies to the sliding-expiration refresh of the cookie, not only to the sign-in.
/// </summary>
public sealed class LoopbackAwareCookieBuilder : CookieBuilder
{
    private static readonly string[] ProxyHeaders = ["X-Forwarded-For", "X-Forwarded-Proto", "Forwarded", "X-Real-IP"];

    public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
    {
        var options = base.Build(context, expiresFrom);
        if (options.Secure && IsPlainLoopbackRequest(context.Request))
            options.Secure = false;
        return options;
    }

    internal static bool IsPlainLoopbackRequest(HttpRequest request)
    {
        if (request.IsHttps) return false;

        // Every header and the Host below can be sent by any peer; only the connection itself shows who is really there. In the desktop app the
        // connection comes from the node's front on the loopback address (the client address the forwarded-headers middleware settles on is the
        // browser's own, also loopback); a stranger on the network never gets the exemption, whatever it claims.
        var peer = request.HttpContext.Connection.RemoteIpAddress;
        if (peer is null || !IPAddress.IsLoopback(peer)) return false;

        foreach (var header in ProxyHeaders)
            if (request.Headers.ContainsKey(header)) return false;

        if (request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHosts) && forwardedHosts.Count > 0)
        {
            foreach (var value in forwardedHosts)
                foreach (var authority in (value ?? "").Split(','))
                    if (!IsLoopbackAuthority(authority)) return false;
            return true;
        }

        return IsLoopbackAuthority(request.Host.Value);
    }

    private static bool IsLoopbackAuthority(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return false;
        var host = new HostString(authority.Trim()).Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1"
            || host == "[::1]";
    }
}
