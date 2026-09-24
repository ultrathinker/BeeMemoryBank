using System.Security.Claims;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// DelegatingHandler that automatically adds X-Internal-Key and X-User-Role
/// headers to every outgoing request from ApiClient to the API server.
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

        var role = httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.IsNullOrEmpty(role))
            request.Headers.TryAddWithoutValidation("X-User-Role", role);

        var userId = httpContextAccessor.HttpContext?.User.FindFirst("UserId")?.Value;
        // F4: GetSecurityStampAsync (OnValidatePrincipal path) sets X-User-Id manually because the
        // principal isn't populated yet. Skip re-adding it when the request already carries one, so
        // we never send a duplicate X-User-Id header (which the API would reject as malformed).
        if (!string.IsNullOrEmpty(userId) && !request.Headers.Contains("X-User-Id"))
            request.Headers.TryAddWithoutValidation("X-User-Id", userId);

        var displayName = httpContextAccessor.HttpContext?.User.FindFirst("DisplayName")?.Value;
        if (!string.IsNullOrEmpty(displayName))
            request.Headers.TryAddWithoutValidation("X-User-DisplayName", displayName);

        // Per-sign-in id (see LoginModel): the API scopes its protected-article unlock cache by it.
        // Cookies minted before this claim existed carry none — the API then just re-prompts.
        var webSession = httpContextAccessor.HttpContext?.User.FindFirst(WebSessionClaim)?.Value;
        if (!string.IsNullOrEmpty(webSession))
            request.Headers.TryAddWithoutValidation("X-Web-Session", webSession);

        return base.SendAsync(request, cancellationToken);
    }
}
