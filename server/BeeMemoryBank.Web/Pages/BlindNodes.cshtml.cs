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
    public string? WarningMessage { get; set; }

    public async Task OnGetAsync(string? msg, string? err, string? warn)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        WarningMessage = warn;
        Nodes = await api.ListBlindNodesAsync();
    }

    public async Task<IActionResult> OnPostAddAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return RedirectToPage(new { err = "Paste the pair code shown by the blind node." });
        var (ok, error, warnings) = await api.AddBlindNodeAsync(code.Trim());
        if (!ok) return RedirectToPage(new { err = error });
        return warnings.Count > 0
            ? RedirectToPage(new { msg = "Blind node added and seeded.", warn = string.Join(" ", warnings) })
            : RedirectToPage(new { msg = "Blind node added and seeded." });
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
