using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// The encryption page of the Admin (docs/design/step-b-spec.md 10.1, 11): "Encrypt this node" with its preflight and its erase of unencrypted
/// copies, and the auto-unlock of this node by its role - the OS card on a desktop, the "Start unlocked after a restart"
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

    /// <summary>The heading of the card of the state of the database and its encryption (and of "Encrypt this node" while it is a plain file).</summary>
    public const string StateCard = "card-database-encryption";

    /// <summary>The heading of the card that says whether the node opens by itself.</summary>
    public const string AutoUnlockCard = "card-auto-unlock";

    /// <summary>
    /// The section of the Admin page that switches metadata encryption on, in the words the node uses to name it in its refusal ("Activate the new sync format
    /// first (Admin > Metadata encryption)"): the page turns that name into a link to the card.
    /// </summary>
    public const string ActivationSection = "Admin > Metadata encryption";

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>The card the message of the last action belongs to; empty: the page's own message (at the top).</summary>
    public string? MessageCard { get; set; }

    /// <summary>True when the message of the last action is the one of <paramref name="card"/>, and the page shows that card.</summary>
    public bool MessageIsFor(string card) =>
        !string.IsNullOrEmpty(SuccessMessage ?? ErrorMessage) && string.Equals(MessageCard, card, StringComparison.Ordinal) && CardIsShown(card);

    /// <summary>True when the message goes to the top of the page: it names no card, or one the page does not show now.</summary>
    public bool MessageIsAtTop => string.IsNullOrEmpty(MessageCard) || !CardIsShown(MessageCard);

    private bool CardIsShown(string card) => card switch
    {
        StateCard => Status is not null,
        AutoUnlockCard => Status is not null && !Running,
        _ => false,
    };

    /// <summary>
    /// What the page is called: on a node whose database is encrypted there is nothing left to switch on, so it is that node's storage protection (BMB-305);
    /// while the database is a plain file it is "Database encryption", with its way to "Encrypt this node".
    /// </summary>
    public string PageTitle => Status is { IsSealed: true } ? "Storage protection" : "Database encryption";

    /// <summary>The words of a blocker, and where in them the name of the Admin section stands (-1: it is not named).</summary>
    public static int SectionAt(string message) => message.IndexOf(ActivationSection, StringComparison.Ordinal);

    public VaultStatusDto? Status { get; private set; }
    public SealPreflightDto? Preflight { get; private set; }
    public ServerAutoUnlockDto? ServerAutoUnlock { get; private set; }
    public bool OsAutoUnlockEnabled { get; private set; }
    public bool OsAutoUnlockSupported { get; private set; }

    /// <summary>True when the node says it is encrypting right now: the page then only follows the progress.</summary>
    public bool Running => Status?.Migration?.Phase == "running";

    public string Role => Status?.AutoUnlock?.Role ?? "server";

    /// <summary>Ends a handler: the page opens at <paramref name="card"/> (a fragment) and the message shows in that card.</summary>
    private IActionResult Back(string card, string? msg = null, string? err = null) =>
        RedirectToPage(null, null, new { msg, err, card }, card);

    public async Task OnGetAsync(string? msg = null, string? err = null, string? card = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        MessageCard = card;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Status = await api.GetVaultStatusAsync();
        if (Status is null) return;
        // While the node encrypts it is in maintenance mode and answers only the status and the progress.
        if (!Status.IsSealed && !Running) Preflight = await api.GetSealPreflightAsync();
        if (Running) return;
        if (Role == "server") ServerAutoUnlock = await api.GetServerAutoUnlockAsync();
        else (OsAutoUnlockEnabled, OsAutoUnlockSupported) = await api.GetAutoUnlockStatusAsync();
    }

    public async Task<IActionResult> OnPostSealAsync(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return Back(StateCard, err: "Enter the master password to encrypt this node.");
        var (ok, error) = await api.StartSealAsync(password);
        return ok
            ? Back(StateCard, msg: "Encrypting the database. This page follows it; do not close the node.")
            : Back(StateCard, err: error);
    }

    public async Task<IActionResult> OnPostEraseAsync(string? password, string[]? paths)
    {
        if (string.IsNullOrEmpty(password) || paths is not { Length: > 0 })
            return Back(StateCard, err: "Tick the copies to erase and enter the master password.");
        var (result, error) = await api.ErasePlainCopiesAsync(password, paths);
        if (result is null) return Back(StateCard, err: error);
        var message = $"{result.Erased.Count + result.UnlinkedOnly.Count} unencrypted copies were erased.";
        if (result.Refused.Count > 0 || result.NotErased.Count > 0)
            message += $" {result.Refused.Count + result.NotErased.Count} could not be erased: " +
                       string.Join(", ", result.Refused.Select(r => $"{r.Path} ({r.Kind})").Concat(result.NotErased)) + ".";
        return Back(StateCard, msg: message);
    }

    public async Task<IActionResult> OnPostEnableServerAutoUnlockAsync(string? password, string? confirmation)
    {
        var (ok, error) = await api.EnableServerAutoUnlockAsync(password ?? "", confirmation ?? "");
        return ok
            ? Back(AutoUnlockCard, msg: "This server will open its vault by itself after a restart.")
            : Back(AutoUnlockCard, err: error);
    }

    public async Task<IActionResult> OnPostDisableServerAutoUnlockAsync(string? password)
    {
        var (ok, error) = await api.DisableServerAutoUnlockAsync(password ?? "");
        return ok
            ? Back(AutoUnlockCard, msg: "After a restart this server stays locked until the master password is entered.")
            : Back(AutoUnlockCard, err: error);
    }

    public async Task<IActionResult> OnPostEnableOsAutoUnlockAsync()
    {
        var (ok, error) = await api.EnableAutoUnlockAsync();
        return ok ? Back(AutoUnlockCard, msg: "Auto-unlock is on.") : Back(AutoUnlockCard, err: error);
    }

    public async Task<IActionResult> OnPostDisableOsAutoUnlockAsync()
    {
        var (ok, error) = await api.DisableAutoUnlockAsync();
        return ok ? Back(AutoUnlockCard, msg: "Auto-unlock is off.") : Back(AutoUnlockCard, err: error);
    }
}
