using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// The Admin "Recovery keys" page (release 2.12): issue a key (a script of <c>admin.js</c> asks <c>/api-proxy/keys/add-recovery</c> and shows
/// the key once), list every recovery key of the node with its owner, and revoke one key or all keys with no owner. A recovery key is personal:
/// it belongs to the superadmin who issued it and resets only that person's password.
/// </summary>
public class RecoveryKeyModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>Every recovery key of this node (owner, date, label — never key material); null when the node did not answer.</summary>
    public List<RecoveryKeyListItemDto>? RecoveryKeys { get; private set; }

    /// <summary>The signed-in person, so the page can tell their own keys from another person's (revoking another's asks for a confirmation naming them).</summary>
    public string? CurrentUserId => User.FindFirst("UserId")?.Value;

    public async Task OnGetAsync()
    {
        await LoadRecoveryKeyStatusAsync();
        RecoveryKeys = await Api.GetRecoveryKeysAsync();
    }

    public async Task<IActionResult> OnPostRevokeKeyAsync(int slotId)
    {
        var (ok, err) = await Api.RevokeRecoveryKeyAsync(slotId);
        return ok
            ? Back(msg: "Recovery key revoked. It can no longer reset a password or open the vault.")
            : Back(err: err ?? "Failed to revoke the recovery key.");
    }

    public async Task<IActionResult> OnPostRevokeUnknownKeysAsync()
    {
        var (count, err) = await Api.RevokeUnknownRecoveryKeysAsync();
        if (err != null) return Back(err: err);
        return Back(msg: count == 0
            ? "There are no recovery keys with an unknown owner."
            : $"Revoked {count} recovery key(s) issued before 2.12 whose owner is unknown.");
    }
}
