using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize(Roles = "superadmin")]
public class RolesModel(ApiClient api) : PageModel
{
    public List<RoleDto>? Roles { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        Roles = await api.GetRolesAsync();
        return Page();
    }
}
