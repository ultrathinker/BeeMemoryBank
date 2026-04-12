using System.Security.Claims;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// DelegatingHandler that automatically adds X-Internal-Key and X-User-Role
/// headers to every outgoing request from ApiClient to the API server.
/// </summary>
public class InternalKeyHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
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
        if (!string.IsNullOrEmpty(userId))
            request.Headers.TryAddWithoutValidation("X-User-Id", userId);

        var displayName = httpContextAccessor.HttpContext?.User.FindFirst("DisplayName")?.Value;
        if (!string.IsNullOrEmpty(displayName))
            request.Headers.TryAddWithoutValidation("X-User-DisplayName", displayName);

        return base.SendAsync(request, cancellationToken);
    }
}
