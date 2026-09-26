using System.Security.Claims;
using BeeMemoryBank.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Issues the web session cookie for a successful API login. Shared by the login page and the
/// setup wizard, which signs the new admin in straight after creating or joining.
/// </summary>
public static class WebSignIn
{
    /// <summary>Key of the per-user security-stamp cache that OnValidatePrincipal reads.</summary>
    public static string StampCacheKey(string userId) => $"security_stamp_{userId}";

    /// <summary>How long OnValidatePrincipal trusts a cached stamp before asking the API again.</summary>
    public static readonly TimeSpan StampCacheTtl = TimeSpan.FromMinutes(5);

    public static Task SignInAsync(HttpContext context, LoginResult result)
    {
        // The API has just handed over the user's CURRENT stamp, so it replaces whatever the cache
        // holds. Without this a password change (which rotates the stamp) left the old stamp cached
        // for up to five minutes, and every fresh sign-in with the new password was rejected on the
        // very next request and bounced back to /Login with no error (BMB-31, scenario 17).
        if (!string.IsNullOrEmpty(result.UserId) && !string.IsNullOrEmpty(result.SecurityStamp))
            context.RequestServices.GetRequiredService<IMemoryCache>().Set(
                StampCacheKey(result.UserId), result.SecurityStamp,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = StampCacheTtl });

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, result.Username!),
            new(ClaimTypes.Role, result.Role!),
            new("DisplayName", result.DisplayName ?? result.Username!),
            new("UserId", result.UserId ?? ""),
            // Node-local security stamp. OnValidatePrincipal revalidates this against the API
            // periodically; a mismatch (password/role change, deletion) rejects the cookie, and
            // so does a cookie without the claim (→ re-login).
            new("SecurityStamp", result.SecurityStamp ?? ""),
            // Random per-sign-in id, forwarded to the API as X-Web-Session. It scopes the
            // protected-article unlock window to this browser's login, so unlocking an article here
            // does not also open it on another device or browser signed in as the same user.
            new(InternalKeyHandler.WebSessionClaim, Guid.NewGuid().ToString("N"))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "BeeWebCookie"));

        return context.SignInAsync("BeeWebCookie", principal,
            new AuthenticationProperties { IsPersistent = true });
    }
}
