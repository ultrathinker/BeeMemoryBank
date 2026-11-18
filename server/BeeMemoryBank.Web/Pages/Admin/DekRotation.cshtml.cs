using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class DekRotationModel(ApiClient api) : AdminPageModel(api)
{
    public DekRotationProgressDto? DekRotationProgress { get; private set; }

    /// <summary>The commit of a rotation this node proposed and has not confirmed yet; the page asks for the master password to begin the re-encryption.</summary>
    public string? DekRotationCommitId { get; private set; }

    /// <summary>The bridge from an old master key (2.12): the old-format doors a rotation could not re-seal; null when the API did not answer.</summary>
    public DekBridgeDto? Bridge { get; private set; }

    /// <summary>A rotation is under way (not idle, not finished, not failed): the page refreshes itself every few seconds.</summary>
    public bool Active => DekRotationProgress != null
        && DekRotationProgress.CurrentStep != "Idle"
        && DekRotationProgress.CurrentStep != "Completed"
        && DekRotationProgress.CurrentStep != "Failed";

    public async Task OnGetAsync()
    {
        DekRotationCommitId = TempData.Peek("DekRotationCommitId") as string;
        await Task.WhenAll(
            Api.GetDekRotationProgressAsync().ContinueWith(t => DekRotationProgress = t.Result),
            Api.GetDekBridgeAsync().ContinueWith(t => Bridge = t.Result),
            LoadPeerPendingRotationsAsync());
    }

    public async Task<IActionResult> OnPostRemoveDekBridgeAsync()
    {
        var (ok, removed, err) = await Api.RemoveDekBridgeAsync();
        if (!ok) return Back(err: err ?? "The bridge could not be removed");
        return Back(msg: removed == 0
            ? "There was no bridge from an old master key on this node."
            : $"The bridge was removed: {removed} old-format door(s) deleted. Those people set their passwords again.");
    }

    public async Task<IActionResult> OnPostProposeDekRotationAsync(string masterPassword)
    {
        var (ok, commitId, err) = await Api.ProposeDekRotationAsync(masterPassword);
        if (ok)
        {
            TempData["DekRotationCommitId"] = commitId;
            return Back(msg: "DEK rotation proposed. Confirm to begin.");
        }
        return Back(err: err ?? "Failed to propose DEK rotation");
    }

    public async Task<IActionResult> OnPostAcceptDekRotationAsync(string commitEventId, string masterPassword)
    {
        var (ok, err) = await Api.AcceptDekRotationAsync(commitEventId, masterPassword);
        if (ok)
        {
            TempData.Remove("DekRotationCommitId");
            return Back(msg: "DEK rotation accepted. Re-encryption is in progress.");
        }
        TempData["DekRotationCommitId"] = commitEventId;
        return Back(err: err ?? "Failed to accept DEK rotation");
    }

    public async Task<IActionResult> OnPostCancelDekRotationAsync(string eventId)
    {
        var (ok, err) = await Api.CancelDekRotationAsync(eventId);
        TempData.Remove("DekRotationCommitId");
        return ok
            ? Back(msg: "DEK rotation cancelled.")
            : Back(err: err ?? "Failed to cancel DEK rotation");
    }

    public Task<IActionResult> OnPostPeerAcceptDekRotationAsync(string eventId) => PeerAcceptDekRotationAsync(eventId);

    public Task<IActionResult> OnPostPeerRejectDekRotationAsync(string eventId) => PeerRejectDekRotationAsync(eventId);
}
