using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class ResetNodeModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }

    /// <summary>
    /// Wipes this node back to the pre-Setup state. Lives here — behind the area's
    /// <c>[Authorize(Roles = "superadmin")]</c> — and never on an anonymous page: with the master
    /// password as the only credential, an unauthenticated visitor could grind it and, on a
    /// correct guess, destroy the node. The password is still required as
    /// re-authentication (the API verifies it without unlocking the vault); a locked-out admin with
    /// no way to sign in uses `bmb init reset` on the host instead — see docs/deployment.md.
    /// </summary>
    public async Task<IActionResult> OnPostResetNodeAsync(string masterPassword)
    {
        if (string.IsNullOrWhiteSpace(masterPassword))
            return Back(err: "Master password is required to reset the node.");

        var (ok, err) = await Api.ResetNodeAsync(masterPassword);
        return ok
            ? Redirect("/Setup?msg=Node+reset+complete")
            : Back(err: err ?? "Reset failed");
    }
}
