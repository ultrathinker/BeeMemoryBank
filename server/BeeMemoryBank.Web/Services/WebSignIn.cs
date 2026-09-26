using System.Security.Claims;
using BeeMemoryBank.Web.Models;
using Microsoft.AspNetCore.Authentication;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Issues the web session cookie for a successful API login. Shared by the login page and the
/// setup wizard, which signs the new admin in straight after creating or joining.
/// </summary>
public static class WebSignIn
{
    public static Task SignInAsync(HttpContext context, LoginResult result)
    {
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
