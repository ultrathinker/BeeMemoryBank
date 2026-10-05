using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Node;

/// <summary>
/// Whether a request carries any header a reverse proxy adds when it forwards a client's request.
/// </summary>
/// <remarks>
/// The front's <c>/node/*</c> endpoints are meant for callers on this machine and decide that by the socket's remote address. A reverse
/// proxy on the same machine (the setup docs/internet-access.md describes) makes every internet visitor arrive from 127.0.0.1, so the
/// address alone cannot tell them apart. The desktop shell, the CLI and the web layer call these endpoints directly and send none of
/// these headers; a request that has one of them was relayed by something, and these endpoints answer it 404 like an off-machine caller.
/// </remarks>
internal static class ForwardingHeaders
{
    public static readonly string[] Names =
    [
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "Forwarded",
        "X-Real-IP",
    ];

    public static bool IsPresentOn(HttpRequest request)
    {
        foreach (var name in Names)
        {
            if (request.Headers.ContainsKey(name)) return true;
        }
        return false;
    }
}
