using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize(Roles = "superadmin")]
public class UsersModel(ApiClient api) : PageModel
{
    public List<UserDto>? Users { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        Users = await api.GetUsersAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var (ok, err, _) = await api.DeleteUserAsync(id);
        return ok
            ? RedirectToPage(new { msg = "User deleted" })
            : RedirectToPage(new { err = err ?? "Failed to delete user" });
    }
}
