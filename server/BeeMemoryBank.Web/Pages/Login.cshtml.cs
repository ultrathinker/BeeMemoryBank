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

    // Set by /RecoverAccess (?recovered=1) and carried through the form: the person who just reset
    // a password with a recovery key is reminded, after signing in, to issue a fresh one.
    [BindProperty]
    public bool Recovered { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>The hint shown after "Show my password hint", or null when there is none to show.</summary>
    public string? ShownHint { get; private set; }

    /// <summary>The one sentence for "no hint" (an unknown name and a user without one read alike).</summary>
    public string? HintMessage { get; private set; }

    /// <summary>True once "Show my password hint" was submitted, so the panel stays open with its answer.</summary>
    public bool HintAttempted { get; private set; }

    /// <summary>True when the node's database is encrypted and closed: the password opens it (docs/design/step-b-spec.md 4.5, 5.6).</summary>
    public bool EncryptedAndClosed { get; private set; }

    public RestoreProgressDto? RestoreProgress { get; set; }
    public DekRotationProgressDto? DekRotationProgress { get; set; }

    /// <summary>
    /// Set when the user comes here from a restore of this node alone: the page then says what that did (the node
    /// is a new one) and what is left to do. Only the one known value counts — it is a link, not a message.
    /// </summary>
    public bool RestoredAsNewNode { get; private set; }

    /// <summary>The identity the node has now; shown beside the notice. Null when the API did not answer.</summary>
    public NodeIdentityDto? RestoredIdentity { get; private set; }

    public async Task<IActionResult> OnGetAsync(bool restore = false, string? returnUrl = null, bool recovered = false, string? restored = null)
    {
        ReturnUrl = returnUrl ?? "/Tree";
        Recovered = recovered;
        EncryptedAndClosed = (await api.GetVaultStatusAsync())?.IsClosed ?? false;
        RestoreProgress = await api.GetRestoreProgressAsync();
        DekRotationProgress = await api.GetDekRotationProgressAsync();
        await ShowRestoredNoticeAsync(restored);
        return Page();
    }

    // The form posts back to the same address, query included, so a mistyped password keeps the notice on screen.
    private async Task ShowRestoredNoticeAsync(string? restored)
    {
        if (!string.Equals(restored, RestoreModes.Standalone, StringComparison.Ordinal)) return;
        RestoredAsNewNode = true;
        try { RestoredIdentity = await api.GetIdentityAsync(); }
        catch (HttpRequestException) { /* the notice is still true without the id */ }
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

    /// <summary>
    /// "Show my password hint": asks the node for the hint of <paramref name="hintUsername"/>. Anonymous by design;
    /// the node - not this page - decides whether there is a hint, and answers an unknown name and a user without
    /// one identically. The hint is rendered with Razor's encoding (it is text a person or an admin typed).
    /// </summary>
    public async Task<IActionResult> OnPostShowHintAsync(string hintUsername)
    {
        HintAttempted = true;
        Username = hintUsername ?? "";
        ReturnUrl = "/Tree";
        EncryptedAndClosed = (await api.GetVaultStatusAsync())?.IsClosed ?? false;
        RestoreProgress = await api.GetRestoreProgressAsync();
        DekRotationProgress = await api.GetDekRotationProgressAsync();

        hintUsername = hintUsername?.Trim() ?? "";
        if (hintUsername.Length == 0)
        {
            HintMessage = "Enter your username to see the hint you wrote for it.";
            return Page();
        }

        var result = await api.LookupPasswordHintAsync(hintUsername, HttpContext.Connection.RemoteIpAddress?.ToString());
        switch (result.Outcome)
        {
            case PasswordHintLookupOutcome.Found:
                ShownHint = result.Hint;
                break;
            case PasswordHintLookupOutcome.NoHint:
                HintMessage = result.Message ?? "No hint is available for this account. Use a recovery key, or ask an administrator to reset the password.";
                break;
            case PasswordHintLookupOutcome.TooManyAttempts:
                HintMessage = "Too many attempts. Try again in a few minutes.";
                break;
            case PasswordHintLookupOutcome.Invalid:
                HintMessage = result.Message ?? "Enter your username to see the hint you wrote for it.";
                break;
            default:
                HintMessage = "The node could not be reached or is busy. Try again in a moment.";
                break;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter username and password.";
            RestoreProgress = await api.GetRestoreProgressAsync();
            DekRotationProgress = await api.GetDekRotationProgressAsync();
            await ShowRestoredNoticeAsync(Request.Query["restored"]);
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
            await ShowRestoredNoticeAsync(Request.Query["restored"]);
            return Page();
        }

        await WebSignIn.SignInAsync(HttpContext, result);

        // If a legacy "master password" slot was migrated into a synthetic admin user during
        // this very login, stash a one-shot banner so the user understands why they're now
        // signed in under a different (synthetic) username.
        if (!string.IsNullOrEmpty(result.MigratedSyntheticUsername))
        {
            TempData["MigrationBanner"] =
                $"This node was upgraded: the legacy master-password slot was promoted to a regular " +
                $"user named '{result.MigratedSyntheticUsername}'. Rename it via Profile if you'd like.";
        }

        // After a recovery-key reset the old recovery keys are not touched, but the person has just
        // shown that the card is in someone's hands: nudge toward a fresh key. The role check is the
        // page's own gate for the link (Issue new recovery key is a superadmin action).
        if (Recovered && result.Role == BeeMemoryBank.Core.Models.UserRoles.Superadmin)
        {
            TempData["RecoveryKeyReminder"] =
                "You signed in after resetting the password with a recovery key. If the card with that " +
                "key was lost or seen by someone else, issue a new one: Admin \u2192 Recovery key \u2192 Issue new recovery key.";
        }

        if (!string.IsNullOrEmpty(ReturnUrl) && !Url.IsLocalUrl(ReturnUrl))
            ReturnUrl = "/Tree";
        return LocalRedirect(string.IsNullOrEmpty(ReturnUrl) ? "/Tree" : ReturnUrl);
    }
}
