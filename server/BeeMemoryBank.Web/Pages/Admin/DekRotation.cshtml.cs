using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class DekRotationModel(ApiClient api) : AdminPageModel(api)
{
    public DekRotationProgressDto? DekRotationProgress { get; private set; }

    /// <summary>The commit of a rotation this node proposed and has not confirmed yet; the page asks for the master password to begin the re-encryption.</summary>
    public string? DekRotationCommitId { get; private set; }

    /// <summary>True right after a rotation was accepted: every recovery key died with the old data key, and the page says to issue a new one.</summary>
    public bool ShowRecoveryKeyReminder { get; private set; }

    /// <summary>A rotation is under way (not idle, not finished, not failed): the page refreshes itself every few seconds.</summary>
    public bool Active => DekRotationProgress != null
        && DekRotationProgress.CurrentStep != "Idle"
        && DekRotationProgress.CurrentStep != "Completed"
        && DekRotationProgress.CurrentStep != "Failed";

    public async Task OnGetAsync()
    {
        DekRotationCommitId = TempData.Peek("DekRotationCommitId") as string;
        // Its own key: "RecoveryKeyReminder" is the sentence the layout shows after a sign-in with a recovery key, and this flag is not a sentence.
        ShowRecoveryKeyReminder = TempData["DekRotationRecoveryKeyReminder"] as string == "true";
        await Task.WhenAll(
            Api.GetDekRotationProgressAsync().ContinueWith(t => DekRotationProgress = t.Result),
            LoadPeerPendingRotationsAsync());
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
            TempData["DekRotationRecoveryKeyReminder"] = "true";
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
