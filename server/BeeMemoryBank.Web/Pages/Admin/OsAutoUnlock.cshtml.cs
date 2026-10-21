using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class OsAutoUnlockModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>The system this node runs on, as a person names its user account: the node runs on the machine it is served from, so it is this process's own.</summary>
    public static string OsName => OperatingSystem.IsMacOS() ? "macOS" : "Windows";

    public bool OsAutoUnlockEnabled { get; private set; }
    public bool OsAutoUnlockSupported { get; private set; }

    /// <summary>What the node says about its database (docs/design/step-b-spec.md 6.7); null when it did not answer.</summary>
    public VaultStatusDto? VaultStatus { get; private set; }

    /// <summary>The role of this node (spec 11.0): on a <c>server</c> the way to turn this on is the checkbox of the encryption page, not this page.</summary>
    public string VaultRole => VaultStatus?.AutoUnlock?.Role ?? "desktop";

    /// <summary>
    /// True when this page offers the switch: the system has a secret store to use, and the node is not a server (docs/design/step-b-spec.md 11.2: on a server the
    /// checkbox of the encryption page turns it on). A slot made earlier still shows, so that it can be turned off.
    /// </summary>
    public bool Offered => OsAutoUnlockSupported && (VaultRole != "server" || OsAutoUnlockEnabled);

    public async Task OnGetAsync()
    {
        await Task.WhenAll(
            Api.GetAutoUnlockStatusAsync().ContinueWith(t =>
            {
                OsAutoUnlockEnabled = t.Result.Enabled;
                OsAutoUnlockSupported = t.Result.Supported;
            }),
            Api.GetVaultStatusAsync().ContinueWith(t => VaultStatus = t.Result));
    }

    public async Task<IActionResult> OnPostEnableAutoUnlockAsync()
    {
        var (ok, err) = await Api.EnableAutoUnlockAsync();
        return ok
            ? Back(msg: $"OS auto-unlock enabled. Anyone with access to this {OsName} account can now open the vault without a password.")
            : Back(err: err ?? "Failed to enable OS auto-unlock.");
    }

    public async Task<IActionResult> OnPostDisableAutoUnlockAsync()
    {
        var (ok, err) = await Api.DisableAutoUnlockAsync();
        return ok
            ? Back(msg: "OS auto-unlock disabled. The vault will no longer unlock automatically on startup.")
            : Back(err: err ?? "Failed to disable OS auto-unlock.");
    }
}
