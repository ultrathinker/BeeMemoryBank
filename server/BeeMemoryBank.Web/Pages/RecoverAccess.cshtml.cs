using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Forgot your password? Use a recovery key." Reachable without signing in (it is what a person
/// locked out of their own node needs), so the page says as little as it can: every refusal is the
/// same sentence, nothing the person typed except the username is ever written back into the form, and
/// success does not sign anyone in — the node only replaces the password, and signing in with the new
/// one is what unlocks the vault, as always.
///
/// <para>Only a superadmin's password can be reset this way; the node refuses everyone else with the
/// same sentence as a wrong key. The throttling is at two layers: PublicRateLimitMiddleware per client
/// address, and the node per username.</para>
/// </summary>
public class RecoverAccessModel(ApiClient api) : PageModel
{
    // The one sentence for "no": the node sends the same for an unknown name, an ordinary user, a
    // wrong key and a key from another node, and this page does not add anything to it.
    internal const string RefusedMessage = "The username or the recovery key is not correct.";

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string RecoveryKey { get; set; } = "";

    [BindProperty]
    public string NewPassword { get; set; } = "";

    [BindProperty]
    public string ConfirmPassword { get; set; } = "";

    public bool Done { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet(bool done = false)
    {
        Done = done;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(RecoveryKey)
            || string.IsNullOrWhiteSpace(NewPassword) || string.IsNullOrWhiteSpace(ConfirmPassword))
        {
            ErrorMessage = "Please fill in all four fields.";
            return Page();
        }

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "The two new passwords do not match.";
            return Page();
        }

        // The same rules as everywhere (the node checks them too); said here first so a weak password
        // costs no request.
        try { UserService.ValidatePassword(NewPassword); }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        var result = await api.RecoverAccessAsync(
            Username.Trim(), RecoveryKey, NewPassword, HttpContext.Connection.RemoteIpAddress?.ToString());

        switch (result.Outcome)
        {
            case RecoverAccessOutcome.Changed:
                // Post-redirect-get: the done screen cannot be re-submitted, and the 3xx is what
                // clears this address's budget in PublicRateLimitMiddleware.
                return RedirectToPage("/RecoverAccess", new { done = true });
            case RecoverAccessOutcome.TooManyAttempts:
                ErrorMessage = "Too many attempts. Try again in a few minutes.";
                break;
            case RecoverAccessOutcome.Invalid:
                ErrorMessage = result.Message ?? "The new password is not acceptable.";
                break;
            case RecoverAccessOutcome.Unavailable:
                ErrorMessage = "The node could not be reached or is busy. Try again in a moment.";
                break;
            default:
                ErrorMessage = RefusedMessage;
                break;
        }
        return Page();
    }
}
