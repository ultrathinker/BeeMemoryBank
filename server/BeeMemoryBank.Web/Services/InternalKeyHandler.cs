using System.Security.Claims;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// DelegatingHandler that automatically adds X-Internal-Key and the caller's identity headers
/// (X-User-Role, X-User-Id, X-Web-Session) to every outgoing request from ApiClient to the API.
/// </summary>
public class InternalKeyHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    /// <summary>Claim holding the random per-sign-in id forwarded as X-Web-Session.</summary>
    public const string WebSessionClaim = "WebSessionId";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (!string.IsNullOrEmpty(key))
            request.Headers.TryAddWithoutValidation("X-Internal-Key", key);

        // A signed-in person is named by X-User-Id, always. What that person is (role, active or not) is the API's to say: it reads its own tbl_user on
        // every call (CallerIdentityMiddleware) and does not look at the role below, which is only the cookie's statement. A role is never sent without
        // the id it belongs to: to the API a role header with no id is a machine-local caller acting in that role (the tray, the CLI), and the Web
        // layer is not one.
        var claims = httpContextAccessor.HttpContext?.User;
        var userId = claims?.FindFirst("UserId")?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            // GetSecurityStampAsync (OnValidatePrincipal path) sets X-User-Id itself because the
            // principal isn't populated yet. Never add a second one: the API rejects a duplicate
            // X-User-Id header as malformed.
            if (!request.Headers.Contains("X-User-Id"))
                request.Headers.TryAddWithoutValidation("X-User-Id", userId);

            var role = claims?.FindFirst(ClaimTypes.Role)?.Value;
            if (!string.IsNullOrEmpty(role))
                request.Headers.TryAddWithoutValidation("X-User-Role", role);
        }

        // The user's display name is deliberately NOT forwarded in a header. A display name is
        // user-controlled text and may be non-ASCII, and SocketsHttpHandler refuses non-ASCII
        // header values at the socket layer — a user whose name is not ASCII would fail EVERY
        // Web→API call before it left the machine. The API resolves the name from the trusted
        // X-User-Id instead (see HttpActorProvider.ActorName); X-User-DisplayName is still stripped
        // defensively from inbound traffic by the node front.

        // Per-sign-in id (see LoginModel): the API scopes its protected-article unlock cache by it.
        // A cookie without the claim sends none, and the API then just re-prompts.
        var webSession = httpContextAccessor.HttpContext?.User.FindFirst(WebSessionClaim)?.Value;
        if (!string.IsNullOrEmpty(webSession))
            request.Headers.TryAddWithoutValidation("X-Web-Session", webSession);

        return base.SendAsync(request, cancellationToken);
    }
}
