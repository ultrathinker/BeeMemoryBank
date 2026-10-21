using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class MasterPasswordModel(ApiClient api) : AdminPageModel(api)
{
    public async Task OnGetAsync() => await LoadMasterPasswordNoticeAsync();

    public async Task<IActionResult> OnPostChangeMasterPasswordAsync(string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(oldPassword) || string.IsNullOrEmpty(newPassword))
            return Back(err: "Both the current and the new master password are required.");

        var (ok, message) = await Api.ChangeMasterPasswordAsync(oldPassword, newPassword);

        // The API's own sentence is shown verbatim, not replaced with a generic "done": it names
        // how many peers are still on the old password, which is the fact the operator has to act
        // on and the reason this endpoint returns prose at all.
        return ok
            ? Back(msg: message ?? "Master password changed on this node.")
            : Back(err: "Failed to change the master password. Check the current password and that the vault is unlocked.");
    }

    public Task<IActionResult> OnPostDismissPasswordNoticeAsync() => DismissPasswordNoticeAsync();
}
