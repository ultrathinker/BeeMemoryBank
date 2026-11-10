using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// The Dream page (plan 3.9): what the sleep-time pass over the notes found, its runs and their history. Superadmin only, like the
/// API group it reads (<c>/api-proxy/dream</c>): Dream looks at every user's notes. The page itself carries no data; its script
/// reads the API. Filled by package U1.
/// </summary>
[Authorize(Roles = "superadmin")]
public class DreamModel(ApiClient api) : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (!await api.IsUnlockedAsync())
        {
            // The vault is locked: sign out and sign in again, as the Tags and article pages do.
            await HttpContext.SignOutAsync("BeeWebCookie");
            return RedirectToPage("/Login", new { returnUrl = "/Dream" });
        }
        return Page();
    }
}
