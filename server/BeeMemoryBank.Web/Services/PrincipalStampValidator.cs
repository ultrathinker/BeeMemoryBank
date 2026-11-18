using System.Security.Claims;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Revalidates the cookie's embedded security stamp against the API on every AUTHENTICATED request (static files run before auth, so never on CSS/JS).
///
/// <list type="bullet">
/// <item><description>absent stamp claim, or no user id: REJECT, which forces a re-login;</description></item>
/// <item><description>stamp mismatch (password or role change, deletion): REJECT;</description></item>
/// <item><description>HTTP 404, the user is definitively gone: REJECT;</description></item>
/// <item><description><b>a request for a superadmin</b> (<see cref="SuperadminRoutes"/>) <b>from a cookie that carries the superadmin role</b>: the stamp is read from the API on every
/// request, never from the cache, and a lookup that cannot be answered (the API is unreachable, answers 5xx, or the body is malformed) REJECTS: a cookie that was a superadmin's is not
/// trusted on a guess. The person signs in again. A cookie without that role is not the case this protects: the role gate refuses it whatever its stamp says, so it takes the ordinary
/// path below and the refusal stays local (the API is not asked for a stamp it cannot use);</description></item>
/// <item><description>any other request: the lookup is cached for <see cref="WebSignIn.StampCacheTtl"/>, and an API that cannot be reached FAILS OPEN (an API hiccup must not log the whole
/// site out; nothing is cached, so revocation resumes at the next lookup). What such a request may do is decided by the API itself, which reads the caller's role from its own
/// table on every call (<c>CallerIdentityMiddleware</c>), so a stale cookie gets a demoted person's rights there, not a superadmin's.</description></item>
/// </list>
/// </summary>
internal static class PrincipalStampValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var stampClaim = principal?.FindFirst("SecurityStamp")?.Value;
        // Absent claim → reject: a stampless cookie cannot be revalidated, so it forces a re-login.
        if (string.IsNullOrEmpty(stampClaim))
        {
            context.RejectPrincipal();
            return;
        }

        var userId = principal?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId) || !int.TryParse(userId, out var userIdInt))
        {
            context.RejectPrincipal();
            return;
        }

        // Without the per-sign-in WebSessionId claim the API never caches a protected-article
        // unlock (no "locks in N min" countdown, every reload re-prompts). Upgrade such a
        // cookie in place instead of forcing a re-login: add a fresh id and re-issue it. Two
        // concurrent requests on the same cookie may each mint one; the last Set-Cookie wins,
        // costing at worst one extra passphrase prompt. A later RejectPrincipal still wins.
        if (principal!.FindFirst(InternalKeyHandler.WebSessionClaim) == null
            && principal.Identity is ClaimsIdentity oldIdentity)
        {
            // Copy constructor: keeps the auth type, name/role claim types and any actor.
            var upgraded = new ClaimsIdentity(oldIdentity);
            upgraded.AddClaim(new Claim(InternalKeyHandler.WebSessionClaim, Guid.NewGuid().ToString("N")));
            principal = new ClaimsPrincipal(upgraded);
            context.ReplacePrincipal(principal);
            context.ShouldRenew = true;
        }

        var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
        var api = context.HttpContext.RequestServices.GetRequiredService<ApiClient>();
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<ApiClient>>();

        // Only a cookie that would pass the role gate needs the strict check: for any other principal the gate (the page's or the proxy route's role requirement) refuses first.
        var strict = principal!.IsInRole(UserRoles.Superadmin) && SuperadminRoutes.IsSuperadminRoute(context.HttpContext);
        var cacheKey = WebSignIn.StampCacheKey(userId);
        string? currentStamp = null;
        if (strict || !cache.TryGetValue(cacheKey, out currentStamp) || currentStamp is null)
        {
            SecurityStampLookup lookup;
            try
            {
                lookup = await api.GetSecurityStampAsync(userIdInt);
            }
            catch (Exception ex)
            {
                lookup = new SecurityStampLookup(SecurityStampLookupOutcome.Unavailable, null);
                logger.LogWarning(ex, "Security-stamp lookup failed for user {UserId}.", userId);
            }

            // HTTP 404 / definitive "user no longer exists" → REJECT. Unlike a transport
            // error, a 404 is an authoritative answer, so failing open would keep a
            // deleted/demoted user's session alive. Not cached (the next request rejects too).
            if (lookup.Outcome == SecurityStampLookupOutcome.NotFound)
            {
                logger.LogWarning("Security-stamp lookup 404 for user {UserId}; rejecting principal.", userId);
                context.RejectPrincipal();
                return;
            }

            // Transport error / 5xx / unreachable / malformed body. A request for a superadmin is not served on a guess: reject, the person signs in
            // again. Any other request FAILS OPEN so an API hiccup does not log everyone out; nothing is cached, so the next request retries.
            if (lookup.Outcome != SecurityStampLookupOutcome.Found || string.IsNullOrEmpty(lookup.Stamp))
            {
                if (strict)
                {
                    logger.LogWarning("Security-stamp lookup for user {UserId} could not be answered on a superadmin route; rejecting principal (fail closed).", userId);
                    context.RejectPrincipal();
                }
                return;
            }

            currentStamp = lookup.Stamp;
            // Only successful (200) lookups are cached.
            cache.Set(cacheKey, currentStamp,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = WebSignIn.StampCacheTtl });
        }

        if (!string.Equals(stampClaim, currentStamp, StringComparison.Ordinal))
            context.RejectPrincipal();
    }
}
