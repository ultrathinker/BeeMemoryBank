using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class ProfileModel : PageModel
{
    public string Username => User.Identity?.Name ?? "";
    public string DisplayName => User.FindFirst("DisplayName")?.Value ?? Username;
    public string Role => User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "user";

    // Base origin the user is currently browsing from (scheme://host[:port]).
    // Honours X-Forwarded-Proto so it is correct behind a reverse proxy with TLS
    // termination, mirroring the pattern in Shared/_HttpsRequiredAlert.cshtml.
    // Used to build ready-to-paste /mcp connection snippets when an agent key is
    // created: the same origin this browser session already uses to reach the app
    // is a reasonable proxy for the URL an agent on this machine/network should use.
    public string McpBaseUrl =>
        $"{(HttpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? HttpContext.Request.Scheme)}://{HttpContext.Request.Host}";
}
