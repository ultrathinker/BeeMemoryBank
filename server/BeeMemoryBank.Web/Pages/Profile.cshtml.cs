using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class ProfileModel : PageModel
{
    public string Username => User.Identity?.Name ?? "";
    public string DisplayName => User.FindFirst("DisplayName")?.Value ?? Username;
    public string Role => User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "user";
}
