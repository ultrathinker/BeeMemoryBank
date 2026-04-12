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

    public async Task<IActionResult> OnPostCreateAsync(string username, string displayName, string password, string role)
    {
        var user = await api.CreateUserAsync(username, displayName, password, role);
        return user != null
            ? RedirectToPage(new { msg = $"User '{user.Username}' created" })
            : RedirectToPage(new { err = "Failed to create user" });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var ok = await api.DeleteUserAsync(id);
        return ok
            ? RedirectToPage(new { msg = "User deleted" })
            : RedirectToPage(new { err = "Failed to delete user" });
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(int id, string newPassword)
    {
        var ok = await api.ChangeUserPasswordAsync(id, newPassword);
        return ok
            ? RedirectToPage(new { msg = "Password changed" })
            : RedirectToPage(new { err = "Failed to change password" });
    }

    public async Task<IActionResult> OnPostUpdateAsync(int id, string displayName, string? role)
    {
        var ok = await api.UpdateUserAsync(id, displayName, role);
        return ok
            ? RedirectToPage(new { msg = "User updated" })
            : RedirectToPage(new { err = "Failed to update user" });
    }
}
