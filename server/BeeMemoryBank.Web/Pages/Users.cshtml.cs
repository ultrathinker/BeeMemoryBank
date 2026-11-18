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
    public SharedDoorsStatusDto? SharedDoors { get; private set; }

    /// <summary>True on a shared node that is NOT the accounts node (superadmins here are read-only mirrors).</summary>
    public bool IsSharedNonHome => SharedDoors is { IsThisNodeShared: true, IsThisNodeAccountsHome: false };

    public string AccountsHomeLabel =>
        !string.IsNullOrWhiteSpace(SharedDoors?.AccountsHomeName) && SharedDoors?.AccountsHomeNodeId is { } id
            ? $"{SharedDoors.AccountsHomeName} ({id:D})"
            : SharedDoors?.AccountsHomeName ?? SharedDoors?.AccountsHomeNodeId?.ToString("D") ?? "the accounts node";

    /// <summary>How many superadmins there are: the last one cannot lose the rights (the API refuses it, and the button says so before the click).</summary>
    public int SuperadminCount => Users?.Count(u => u.Role == "superadmin") ?? 0;

    /// <summary>The signed-in person. Nobody takes their own superadmin rights away from here: another superadmin does it.</summary>
    public string? CurrentUserId => User.FindFirst("UserId")?.Value;
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        Users = await api.GetUsersAsync();
        SharedDoors = await api.GetSharedDoorsStatusAsync();
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
