using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// Database encryption (docs/design/step-b-spec.md 10.1, 11; BMB-305): the state of the node's database and what can be done about it. While the database is a
/// plain file the page offers "Encrypt this node" with its preflight and its erase of unencrypted copies; once it is encrypted there is nothing left to switch on and
/// the page is the status of the node's storage protection, which on a server also holds the switch "Start unlocked after a restart" with its two steps. The page is
/// called "Database encryption" in both states (the index says the same: a person who clicks it lands on a page of that name). (Whether a desktop opens by itself is <c>/Admin/OsAutoUnlock</c>.) Every change asks for the master password again.
/// </summary>
public class StorageProtectionModel(ApiClient api) : AdminPageModel(api)
{
    public const string ServerWarning =
        "Strongly not recommended on a server. With this on, the key that opens this server's database is kept on the server itself, so anyone who gets this " +
        "server's disk, a copy of its data folder or a backup of it can read the whole vault: the database encryption no longer protects it.";

    public const string ServerConfirmation =
        "Last check: after a restart this server will open the vault by itself, with nobody signed in. A stolen disk then opens everything.";

    public const string Phrase = "START UNLOCKED";

    /// <summary>
    /// The section of the Admin page that switches metadata encryption on, in the words the node uses to name it in its refusal ("Activate the new sync format
    /// first (Admin > Metadata encryption)"): the page turns that name into a link to the page.
    /// </summary>
    public const string ActivationSection = "Admin > Metadata encryption";

    /// <summary>The words of a blocker, and where in them the name of the Admin section stands (-1: it is not named).</summary>
    public static int SectionAt(string message) => message.IndexOf(ActivationSection, StringComparison.Ordinal);

    /// <summary>Follows "Encrypt this node" while it runs, and walks the two steps of the server switch.</summary>
    public override IReadOnlyList<string> PageScripts => ["/js/pages/vault-encryption.js"];

    /// <summary>What the node says about its database (docs/design/step-b-spec.md 6.7); null when it did not answer.</summary>
    public VaultStatusDto? VaultStatus { get; private set; }
    public SealPreflightDto? Preflight { get; private set; }
    public ServerAutoUnlockDto? ServerAutoUnlock { get; private set; }

    /// <summary>True when the node says it is encrypting right now: the page then only follows the progress.</summary>
    public bool Running => VaultStatus?.Migration?.Phase == "running";

    /// <summary>The role of this node (spec 11.0): <c>server</c> sets whether it opens by itself here; a <c>desktop</c> does it on the page OS Auto-Unlock.</summary>
    public string Role => VaultStatus?.AutoUnlock?.Role ?? "server";

    public async Task OnGetAsync()
    {
        VaultStatus = await Api.GetVaultStatusAsync();
        if (VaultStatus is null) return;
        // While the node encrypts it is in maintenance mode and answers only the status and the progress.
        if (!VaultStatus.IsSealed && !Running) Preflight = await Api.GetSealPreflightAsync();
        if (!Running && Role == "server") ServerAutoUnlock = await Api.GetServerAutoUnlockAsync();
    }

    public async Task<IActionResult> OnPostSealAsync(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return Back(err: "Enter the master password to encrypt this node.");
        var (ok, error) = await Api.StartSealAsync(password);
        return ok
            ? Back(msg: "Encrypting the database. This page follows it; do not close the node.")
            : Back(err: error);
    }

    public async Task<IActionResult> OnPostEraseAsync(string? password, string[]? paths)
    {
        if (string.IsNullOrEmpty(password) || paths is not { Length: > 0 })
            return Back(err: "Tick the copies to erase and enter the master password.");
        var (result, error) = await Api.ErasePlainCopiesAsync(password, paths);
        if (result is null) return Back(err: error);
        var message = $"{result.Erased.Count + result.UnlinkedOnly.Count} unencrypted copies were erased.";
        if (result.Refused.Count > 0 || result.NotErased.Count > 0)
            message += $" {result.Refused.Count + result.NotErased.Count} could not be erased: " +
                       string.Join(", ", result.Refused.Select(r => $"{r.Path} ({r.Kind})").Concat(result.NotErased)) + ".";
        return Back(msg: message);
    }

    public async Task<IActionResult> OnPostEnableServerAutoUnlockAsync(string? password, string? confirmation)
    {
        var (ok, error) = await Api.EnableServerAutoUnlockAsync(password ?? "", confirmation ?? "");
        return ok
            ? Back(msg: "This server will open its vault by itself after a restart.")
            : Back(err: error);
    }

    public async Task<IActionResult> OnPostDisableServerAutoUnlockAsync(string? password)
    {
        var (ok, error) = await Api.DisableServerAutoUnlockAsync(password ?? "");
        return ok
            ? Back(msg: "After a restart this server stays locked until the master password is entered.")
            : Back(err: error);
    }
}
