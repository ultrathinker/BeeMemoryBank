using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// The encryption page of the Admin (docs/design/step-b-spec.md 10.1, 10.7, 11): "Encrypt this node" with its preflight and its erase of unencrypted
/// copies, the way-back snapshot, and the auto-unlock of this node by its role - the OS card on a desktop, the "Start unlocked after a restart"
/// checkbox with its two steps on a server. Superadmin only; every change asks for the master password again.
/// </summary>
[Authorize(Roles = "superadmin")]
public class VaultEncryptionModel(ApiClient api) : PageModel
{
    public const string ServerWarning =
        "Strongly not recommended on a server. With this on, the key that opens this server's database is kept on the server itself, so anyone who gets this " +
        "server's disk, a copy of its data folder or a backup of it can read the whole vault: the database encryption no longer protects it.";

    public const string ServerConfirmation =
        "Last check: after a restart this server will open the vault by itself, with nobody signed in. A stolen disk then opens everything.";

    public const string Phrase = "START UNLOCKED";

    public const string WayBackExplained =
        "The way back is this encrypted snapshot of the node as it was before the encryption. It opens with the master password or recovery key you had " +
        "on {0}, even if you have changed them since. Keep it until you are sure the node works, then delete it.";

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public VaultStatusDto? Status { get; private set; }
    public SealPreflightDto? Preflight { get; private set; }
    public WayBackDto? WayBack { get; private set; }
    public ServerAutoUnlockDto? ServerAutoUnlock { get; private set; }
    public bool OsAutoUnlockEnabled { get; private set; }
    public bool OsAutoUnlockSupported { get; private set; }
    public bool AcceptNoSnapshot { get; private set; }

    /// <summary>True when the node says it is encrypting right now: the page then only follows the progress.</summary>
    public bool Running => Status?.Migration?.Phase == "running";

    public string Role => Status?.AutoUnlock?.Role ?? "server";

    public async Task OnGetAsync(string? msg = null, string? err = null, bool acceptNoSnapshot = false)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        AcceptNoSnapshot = acceptNoSnapshot;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Status = await api.GetVaultStatusAsync();
        if (Status is null) return;
        // While the node encrypts it is in maintenance mode and answers only the status and the progress.
        if (!Status.IsSealed && !Running) Preflight = await api.GetSealPreflightAsync(AcceptNoSnapshot);
        if (Status.IsSealed) WayBack = await api.GetWayBackAsync();
        if (Running) return;
        if (Role == "server") ServerAutoUnlock = await api.GetServerAutoUnlockAsync();
        else (OsAutoUnlockEnabled, OsAutoUnlockSupported) = await api.GetAutoUnlockStatusAsync();
    }

    public async Task<IActionResult> OnPostSealAsync(string? password, bool acceptNoSnapshot)
    {
        if (string.IsNullOrEmpty(password))
            return RedirectToPage(new { err = "Enter the master password to encrypt this node.", acceptNoSnapshot });
        var (ok, error) = await api.StartSealAsync(password, acceptNoSnapshot);
        return ok
            ? RedirectToPage(new { msg = "Encrypting the database. This page follows it; do not close the node." })
            : RedirectToPage(new { err = error, acceptNoSnapshot });
    }

    public async Task<IActionResult> OnPostEraseAsync(string? password, string[]? paths)
    {
        if (string.IsNullOrEmpty(password) || paths is not { Length: > 0 })
            return RedirectToPage(new { err = "Tick the copies to erase and enter the master password." });
        var (result, error) = await api.ErasePlainCopiesAsync(password, paths);
        if (result is null) return RedirectToPage(new { err = error });
        var message = $"{result.Erased.Count + result.UnlinkedOnly.Count} unencrypted copies were erased.";
        if (result.Refused.Count > 0 || result.NotErased.Count > 0)
            message += $" {result.Refused.Count + result.NotErased.Count} could not be erased: " +
                       string.Join(", ", result.Refused.Select(r => $"{r.Path} ({r.Kind})").Concat(result.NotErased)) + ".";
        return RedirectToPage(new { msg = message });
    }

    public async Task<IActionResult> OnPostDeleteWayBackAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) return RedirectToPage(new { err = "Enter the master password to delete the way-back snapshot." });
        var (ok, error) = await api.DeleteWayBackAsync(password);
        return ok
            ? RedirectToPage(new { msg = "The way-back snapshot was deleted. The node can no longer be taken back to its plain state." })
            : RedirectToPage(new { err = error });
    }

    public async Task<IActionResult> OnPostEnableServerAutoUnlockAsync(string? password, string? confirmation)
    {
        var (ok, error) = await api.EnableServerAutoUnlockAsync(password ?? "", confirmation ?? "");
        return ok
            ? RedirectToPage(new { msg = "This server will open its vault by itself after a restart." })
            : RedirectToPage(new { err = error });
    }

    public async Task<IActionResult> OnPostDisableServerAutoUnlockAsync(string? password)
    {
        var (ok, error) = await api.DisableServerAutoUnlockAsync(password ?? "");
        return ok
            ? RedirectToPage(new { msg = "After a restart this server stays locked until the master password is entered." })
            : RedirectToPage(new { err = error });
    }

    public async Task<IActionResult> OnPostEnableOsAutoUnlockAsync()
    {
        var (ok, error) = await api.EnableAutoUnlockAsync();
        return ok ? RedirectToPage(new { msg = "Auto-unlock is on." }) : RedirectToPage(new { err = error });
    }

    public async Task<IActionResult> OnPostDisableOsAutoUnlockAsync()
    {
        var (ok, error) = await api.DisableAutoUnlockAsync();
        return ok ? RedirectToPage(new { msg = "Auto-unlock is off." }) : RedirectToPage(new { err = error });
    }
}
