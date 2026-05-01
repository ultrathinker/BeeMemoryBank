using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class SetupModel(ApiClient api) : PageModel
{
    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostStandaloneAsync(
        string adminUsername, string displayName, string password, string confirmPassword)
    {
        adminUsername = adminUsername?.Trim() ?? "";
        displayName = displayName?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(adminUsername) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "All fields are required.";
            return Page();
        }

        if (password != confirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        var (ok, error) = await api.InitStandaloneAsync(adminUsername, displayName, password);
        if (!ok)
        {
            ErrorMessage = error ?? "Initialization failed.";
            return Page();
        }

        return RedirectToPage("/Login");
    }

    public async Task<IActionResult> OnPostJoinAsync(
        string joinAdminUsername, string joinDisplayName, string remoteUrl, string joinPassword)
    {
        joinAdminUsername = joinAdminUsername?.Trim() ?? "";
        joinDisplayName = joinDisplayName?.Trim() ?? "";
        remoteUrl = remoteUrl?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(joinAdminUsername) ||
            string.IsNullOrWhiteSpace(joinDisplayName) ||
            string.IsNullOrWhiteSpace(remoteUrl) ||
            string.IsNullOrWhiteSpace(joinPassword))
        {
            ErrorMessage = "All fields are required.";
            return Page();
        }

        var (ok, error) = await api.InitJoinAsync(joinAdminUsername, joinDisplayName, remoteUrl, joinPassword);
        if (!ok)
        {
            ErrorMessage = error ?? "Join failed.";
            return Page();
        }

        return RedirectToPage("/Login");
    }
}
