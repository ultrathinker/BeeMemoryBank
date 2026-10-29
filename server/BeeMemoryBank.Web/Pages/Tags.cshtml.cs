using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class TagsModel(ApiClient api) : PageModel
{
    public bool IsSuperadmin { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var isUnlocked = await api.IsUnlockedAsync();
        if (!isUnlocked)
        {
            // The vault is locked: sign out and sign in again, as the article pages do. (This used to go through /Lock, which signed out on a GET.)
            await HttpContext.SignOutAsync("BeeWebCookie");
            return RedirectToPage("/Login", new { returnUrl = "/Tags" });
        }
        IsSuperadmin = User.HasClaim(c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "superadmin");
        return Page();
    }
}
