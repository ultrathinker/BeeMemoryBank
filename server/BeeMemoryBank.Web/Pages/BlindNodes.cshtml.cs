using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Blind nodes" (plan 9): add one by its pair code, see status, protocol and last contact with the
/// alarms of plan 5.6, reseed, disconnect. Superadmin only, like Admin: every action here changes
/// who the network trusts.
/// </summary>
[Authorize(Roles = "superadmin")]
public class BlindNodesModel(ApiClient api) : PageModel
{
    public List<BlindNodeDto>? Nodes { get; set; }
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(string? msg, string? err)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        Nodes = await api.ListBlindNodesAsync();
    }

    public async Task<IActionResult> OnPostAddAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return RedirectToPage(new { err = "Paste the pair code shown by the blind node." });
        var (ok, error) = await api.AddBlindNodeAsync(code.Trim());
        return ok
            ? RedirectToPage(new { msg = "Blind node added and seeded." })
            : RedirectToPage(new { err = error });
    }

    public async Task<IActionResult> OnPostReseedAsync(Guid nodeId)
    {
        var (ok, error) = await api.ReseedBlindNodeAsync(nodeId);
        return ok
            ? RedirectToPage(new { msg = "Blind node reseeded." })
            : RedirectToPage(new { err = error ?? "Reseed failed." });
    }

    public async Task<IActionResult> OnPostDisconnectAsync(Guid nodeId)
    {
        var ok = await api.RevokeNodeAsync(nodeId);
        return ok
            ? RedirectToPage(new { msg = "Blind node disconnected. Wipe it on its own console." })
            : RedirectToPage(new { err = "Failed to disconnect the blind node." });
    }
}
