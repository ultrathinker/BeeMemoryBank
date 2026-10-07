using System.Net;
using System.Text.Json;

namespace BeeMemoryBank.Core.Services;

/// <summary>The three calls a remote account makes to the other node.</summary>
public enum RemoteRoute
{
    /// <summary><c>POST /api/auth/remote-token</c>: user name and password for a long-lived token.</summary>
    RemoteToken,

    /// <summary><c>GET /api/folders/accessible</c>: the folders the guest may mirror.</summary>
    AccessibleFolders,

    /// <summary><c>GET /api/folders/by-path/snapshot</c>: one folder and what is in it, polled every minute.</summary>
    FolderSnapshot,
}

/// <summary>
/// What the person looking at the Remote Accounts page is told when the other node says no. A guest account works
/// only if the other node's reverse proxy forwards three routes to its API (docs/internet-access.md); no shipped
/// configuration does, so the commonest failure is a plain 404 from the other node's web page. A bare
/// "HTTP 404" does not say that, these messages do. Nothing here decides anything about access.
/// </summary>
public static class RemoteAccountErrors
{
    public const string RoutesNotForwarded =
        "The other node does not let guest sign-ins through. Its reverse proxy must forward /api/auth/remote-token, " +
        "/api/folders/accessible and /api/folders/by-path/snapshot: see docs/internet-access.md";

    public const string InvalidAddress =
        "Enter the other node's address as a full https:// name, for example https://bee.example.com.";

    public const string UseHttpsName =
        "This address starts with http://. Use an https:// name for the other node, for example https://bee.example.com " +
        "(plain http:// is only accepted for localhost).";

    public const string PrivateAddress =
        "Use an https:// name for the other node, for example https://bee.example.com. An IP address on a private or " +
        "special network (10.x, 172.16-31.x, 192.168.x, 169.254.x and the like) is refused, so that this node cannot " +
        "be used to probe your own network.";

    public const string Unreachable =
        "This node could not reach the other node at that address. Check the address and that the other node is running.";

    public const string WrongCredentials = "The other node did not accept that user name and password.";

    public const string TooManyAttempts =
        "The other node limits sign-in attempts (5 in 5 minutes). Wait a few minutes and try again.";

    public const string OwnerLocked = "The other node is locked: its owner has to unlock it first.";

    /// <summary>
    /// True when the other node's answer cannot have come from its API for this route, i.e. its reverse proxy (or its
    /// web page) answered instead: 404, 403 or 405. For <see cref="RemoteRoute.RemoteToken"/> that is certain, the
    /// handler answers only 200, 400, 401 or 429; and no handler answers 405. The two folder routes do answer 403
    /// ("Access denied") and 404 ("Folder not found") themselves, but always with a JSON body that has an
    /// <c>error</c> text; an empty or HTML body is the proxy or the web page.
    /// </summary>
    public static async Task<bool> AreRoutesNotForwardedAsync(HttpResponseMessage response, RemoteRoute route)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
            return false;
        return route == RemoteRoute.RemoteToken
            || response.StatusCode == HttpStatusCode.MethodNotAllowed
            || !await IsApiErrorAnswerAsync(response);
    }

    /// <summary>The sentence for a failed sign-in or folder listing. Never contains the other node's body text.</summary>
    public static async Task<string> DescribeAsync(HttpResponseMessage response, RemoteRoute route)
    {
        if (await AreRoutesNotForwardedAsync(response, route)) return RoutesNotForwarded;

        var code = (int)response.StatusCode;
        return (route, response.StatusCode) switch
        {
            (RemoteRoute.RemoteToken, HttpStatusCode.Unauthorized) => WrongCredentials,
            (RemoteRoute.RemoteToken, HttpStatusCode.TooManyRequests) => TooManyAttempts,
            (_, HttpStatusCode.Locked) => OwnerLocked,
            (RemoteRoute.RemoteToken, _) => $"Remote login failed (HTTP {code})",
            _ => $"List accessible folders failed (HTTP {code})",
        };
    }

    private static async Task<bool> IsApiErrorAnswerAsync(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals("error", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
