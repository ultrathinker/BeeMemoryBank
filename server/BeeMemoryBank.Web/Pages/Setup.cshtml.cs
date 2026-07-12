using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class SetupModel(ApiClient api) : PageModel
{
    public string? ErrorMessage { get; set; }

    /// <summary>"" = mode-select (step 1), "form" = show form (step 2), "done" = completion (step 3)</summary>
    public string Step { get; set; } = "";

    /// <summary>"standalone" or "join" — tracks which path the user took, shown in step 3</summary>
    public string Mode { get; set; } = "standalone";

    public void OnGet(string? step, string? mode)
    {
        Step = step ?? "";
        Mode = mode ?? "standalone";
    }

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
            Step = "form";
            Mode = "standalone";
            return Page();
        }

        if (password != confirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            Step = "form";
            Mode = "standalone";
            return Page();
        }

        var (ok, error) = await api.InitStandaloneAsync(adminUsername, displayName, password);
        if (!ok)
        {
            ErrorMessage = error ?? "Initialization failed.";
            Step = "form";
            Mode = "standalone";
            return Page();
        }

        return RedirectToPage("/Setup", new { step = "done", mode = "standalone" });
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
            Step = "form";
            Mode = "join";
            return Page();
        }

        var (ok, error) = await api.InitJoinAsync(joinAdminUsername, joinDisplayName, remoteUrl, joinPassword);
        if (!ok)
        {
            ErrorMessage = error ?? "Join failed.";
            Step = "form";
            Mode = "join";
            return Page();
        }

        return RedirectToPage("/Setup", new { step = "done", mode = "join" });
    }
}
