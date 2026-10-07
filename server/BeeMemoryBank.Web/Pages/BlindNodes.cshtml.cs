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

    /// <summary>The full nodes blind copies can be told to call (a hub, ADR 0007), with the trust mode of each.</summary>
    public List<BlindPhoneListenerDto>? Hubs { get; set; }
    public string? SuccessMessage { get; set; }
    public BlindNodeError? Error { get; set; }
    public string? WarningMessage { get; set; }

    /// <summary>The pair code the operator pasted, given back in the box when the add failed.</summary>
    public string? Code { get; set; }

    public async Task OnGetAsync(string? msg, string? err, string? warn, string? detail)
    {
        SuccessMessage = msg;
        WarningMessage = warn;
        if (!string.IsNullOrEmpty(err)) Error = BlindNodeError.Of(err, detail);
        Nodes = await api.ListBlindNodesAsync();
        Hubs = await LoadHubsAsync();
    }

    private async Task<List<BlindPhoneListenerDto>?> LoadHubsAsync() =>
        (await api.ListBlindPhoneListenersAsync())?.Where(l => !l.IsBlind).ToList();

    public async Task<IActionResult> OnPostAddAsync(string code)
    {
        // A failed add shows the page again, right here, with the code still in the box: the
        // operator fixes what the message says and presses Add, instead of pasting the code afresh.
        if (string.IsNullOrWhiteSpace(code))
            return await ShowAsync(BlindNodeErrors.EmptyCode(), code);
        var (ok, error, warnings) = await api.AddBlindNodeAsync(code.Trim());
        if (!ok) return await ShowAsync(error, code.Trim());
        return warnings.Count > 0
            ? RedirectToPage(new { msg = "Blind node added and seeded.", warn = string.Join(" ", warnings) })
            : RedirectToPage(new { msg = "Blind node added and seeded." });
    }

    public async Task<IActionResult> OnPostReseedAsync(Guid nodeId)
    {
        var (ok, error) = await api.ReseedBlindNodeAsync(nodeId);
        return ok
            ? RedirectToPage(new { msg = "Blind node reseeded." })
            : RedirectToPage(new { err = error!.Message, detail = error.Detail });
    }

    public async Task<IActionResult> OnPostDisconnectAsync(Guid nodeId)
    {
        bool ok;
        try
        {
            ok = await api.RevokeNodeAsync(nodeId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var down = BlindNodeErrors.AppDidNotAnswer(ex);
            return RedirectToPage(new { err = down.Message, detail = down.Detail });
        }
        if (ok) return RedirectToPage(new { msg = "Blind node disconnected. Wipe it on its own console." });
        var failed = BlindNodeErrors.ForDisconnect();
        return RedirectToPage(new { err = failed.Message });
    }

    private async Task<IActionResult> ShowAsync(BlindNodeError? error, string? code)
    {
        Error = error;
        Code = code;
        Nodes = await api.ListBlindNodesAsync();
        Hubs = await LoadHubsAsync();
        return Page();
    }
}
