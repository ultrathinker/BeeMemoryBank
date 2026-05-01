using System.Security.Claims;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class LoginModel(ApiClient api) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string ReturnUrl { get; set; } = "/Tree";

    public string? ErrorMessage { get; set; }

    public RestoreProgressDto? RestoreProgress { get; set; }
    public DekRotationProgressDto? DekRotationProgress { get; set; }

    public async Task<IActionResult> OnGetAsync(bool restore = false, string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? "/Tree";
        RestoreProgress = await api.GetRestoreProgressAsync();
        DekRotationProgress = await api.GetDekRotationProgressAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostContinueWithoutBackupAsync(Guid eventId, string masterPassword)
    {
        var ok = await api.ContinueRestoreWithoutBackupAsync(eventId, masterPassword);
        if (!ok)
        {
            ErrorMessage = "Invalid master password";
            RestoreProgress = await api.GetRestoreProgressAsync();
            DekRotationProgress = await api.GetDekRotationProgressAsync();
            return Page();
        }
        return RedirectToPage("/Login", new { restore = true });
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter username and password.";
            RestoreProgress = await api.GetRestoreProgressAsync();
            DekRotationProgress = await api.GetDekRotationProgressAsync();
            return Page();
        }

        var result = await api.LoginAsync(Username, Password);

        if (!result.Success)
        {
            if (result.IsLocked)
                ErrorMessage = "Server is locked. Contact administrator.";
            else
                ErrorMessage = result.Error ?? "Invalid username or password.";
            RestoreProgress = await api.GetRestoreProgressAsync();
            DekRotationProgress = await api.GetDekRotationProgressAsync();
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, result.Username!),
            new(ClaimTypes.Role, result.Role!),
            new("DisplayName", result.DisplayName ?? result.Username!),
            new("UserId", result.UserId ?? "")
        };
        var identity = new ClaimsIdentity(claims, "BeeWebCookie");
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync("BeeWebCookie", principal,
            new AuthenticationProperties { IsPersistent = true });

        // If a legacy "master password" slot was migrated into a synthetic admin user during
        // this very login, stash a one-shot banner so the user understands why they're now
        // signed in under a different (synthetic) username.
        if (!string.IsNullOrEmpty(result.MigratedSyntheticUsername))
        {
            TempData["MigrationBanner"] =
                $"This node was upgraded: the legacy master-password slot was promoted to a regular " +
                $"user named '{result.MigratedSyntheticUsername}'. Rename it via Profile if you'd like.";
        }

        if (!string.IsNullOrEmpty(ReturnUrl) && !Url.IsLocalUrl(ReturnUrl))
            ReturnUrl = "/Tree";
        return LocalRedirect(string.IsNullOrEmpty(ReturnUrl) ? "/Tree" : ReturnUrl);
    }

    public async Task<IActionResult> OnPostResetAsync(string masterPassword)
    {
        if (string.IsNullOrWhiteSpace(masterPassword))
        {
            ErrorMessage = "Master password required";
            RestoreProgress = await api.GetRestoreProgressAsync();
            DekRotationProgress = await api.GetDekRotationProgressAsync();
            return Page();
        }
        var (ok, err) = await api.ResetNodeAsync(masterPassword);
        if (ok)
            return Redirect("/Setup?msg=Node+reset+complete");
        ErrorMessage = err ?? "Reset failed";
        RestoreProgress = await api.GetRestoreProgressAsync();
        DekRotationProgress = await api.GetDekRotationProgressAsync();
        return Page();
    }
}
