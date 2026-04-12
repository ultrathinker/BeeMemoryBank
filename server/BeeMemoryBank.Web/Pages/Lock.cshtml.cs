using System.Security.Claims;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class LockModel(ApiClient api) : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role != "superadmin")
            return Forbid();

        await api.LockAsync();
        await HttpContext.SignOutAsync("BeeWebCookie");
        return Redirect("/Login");
    }
}
