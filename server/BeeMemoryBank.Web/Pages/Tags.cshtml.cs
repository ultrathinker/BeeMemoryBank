using BeeMemoryBank.Web.Services;
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
            return RedirectToPage("/Lock");
        }
        IsSuperadmin = User.HasClaim(c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "superadmin");
        return Page();
    }
}
