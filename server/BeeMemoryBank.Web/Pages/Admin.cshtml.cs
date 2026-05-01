using System.Text.Json;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize(Roles = "superadmin")]
public class AdminModel(ApiClient api) : PageModel
{
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public NodeIdentityDto? Identity { get; set; }
    public List<WhitelistEntryDto>? Whitelist { get; set; }
    public Dictionary<Guid, DateTime>? NodeSyncStatus { get; set; }
    public List<SnapshotDto>? Snapshots { get; set; }
    public List<AgentDto>? Agents { get; set; }
    public List<UserDto>? Users { get; set; }
    public JsonElement? GraphEdgeStats { get; set; }
    public string? GraphRebuildReport { get; set; }
    public CompactionPreviewDto? CompactionPreview { get; set; }
    public List<SnapshotCheckpointDto>? SnapshotCheckpoints { get; set; }
    public DekRotationProgressDto? DekRotationProgress { get; set; }
    public string? DekRotationCommitId { get; set; }
    public bool ShowRecoveryKeyReminder { get; set; }
    public List<PeerPendingDekRotationDto> PeerPendingRotations { get; set; } = new();

    public async Task OnGetAsync(string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        GraphRebuildReport = TempData["GraphRebuildReport"] as string;
        DekRotationCommitId = TempData.Peek("DekRotationCommitId") as string;
        ShowRecoveryKeyReminder = TempData["RecoveryKeyReminder"] as string == "true";
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnPostRevokeNodeAsync(Guid nodeId)
    {
        var ok = await api.RevokeNodeAsync(nodeId);
        return ok
            ? RedirectToPage(new { msg = "Node access revoked" })
            : RedirectToPage(new { err = "Failed to revoke node access" });
    }

    public async Task<IActionResult> OnPostChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var (ok, error) = await api.ChangeNodeAddressAsync(nodeId, newApiAddress, password);
        return ok
            ? RedirectToPage(new { msg = "Node address updated" })
            : RedirectToPage(new { err = error ?? "Failed to update node address" });
    }

    public async Task<IActionResult> OnPostSetAutoAcceptRestoreAsync(Guid nodeId, bool autoAccept)
    {
        var (ok, error) = await api.SetAutoAcceptRestoreAsync(nodeId, autoAccept);
        if (!ok)
        {
            ErrorMessage = "Failed to update auto-accept setting: " + (error ?? "unknown");
            await LoadDataAsync();
            return Page();
        }
        SuccessMessage = autoAccept
            ? "Auto-accept restore enabled — restores from this peer will apply automatically."
            : "Auto-accept restore disabled — restores from this peer will require manual approval.";
        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetAutoAcceptDekRotationAsync(Guid nodeId, bool autoAccept)
    {
        var (ok, error) = await api.SetAutoAcceptDekRotationAsync(nodeId, autoAccept);
        if (!ok)
        {
            ErrorMessage = "Failed to update auto-accept DEK rotation setting: " + (error ?? "unknown");
            await LoadDataAsync();
            return Page();
        }
        SuccessMessage = autoAccept
            ? "Auto-accept DEK rotation enabled — DEK rotations from this peer will apply automatically."
            : "Auto-accept DEK rotation disabled — DEK rotations from this peer will require manual approval.";
        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateSnapshotAsync()
    {
        var snap = await api.CreateSnapshotAsync();
        return snap != null
            ? RedirectToPage(new { msg = "Snapshot created" })
            : RedirectToPage(new { err = "Failed to create snapshot" });
    }

    public async Task<IActionResult> OnPostDeleteSnapshotAsync(string fileName)
    {
        var ok = await api.DeleteSnapshotAsync(fileName);
        return ok
            ? RedirectToPage(new { msg = "Snapshot deleted" })
            : RedirectToPage(new { err = "Failed to delete snapshot" });
    }

    public async Task<IActionResult> OnPostUploadSnapshotAsync(IFormFile file)
    {
        var result = await api.UploadSnapshotAsync(file);
        if (result == null) return BadRequest("Upload failed");
        return new JsonResult(result);
    }

    public async Task<IActionResult> OnPostInitiateNetworkRestoreAsync(string fileName)
    {
        var snapshots = await api.GetSnapshotsAsync() ?? new();
        var snap = snapshots.FirstOrDefault(s => s.FileName == fileName);
        if (snap == null) return NotFound();

        var (ok, eventId, error) = await api.InitiateNetworkRestoreAsync(snap.FileId ?? Guid.Empty);
        if (!ok) return BadRequest(error);
        return new JsonResult(new { eventId });
    }

    public async Task<IActionResult> OnGetDownloadSnapshotAsync(string fileName)
    {
        var result = await api.DownloadSnapshotAsync(fileName);
        if (result == null || !result.IsSuccessStatusCode)
            return RedirectToPage(new { err = "Failed to download snapshot" });

        var stream = await result.Content.ReadAsStreamAsync();
        return new FileStreamResult(new DisposingStreamWrapper(stream, result), "application/gzip") { FileDownloadName = fileName };
    }

    public async Task<IActionResult> OnPostRestoreSnapshotAsync(
        string fileName, string masterPassword, bool createBackupFirst = true, bool standaloneMode = false)
    {
        var (ok, error, backupFileName) = await api.RestoreSnapshotAsync(
            fileName, masterPassword, createBackupFirst, standaloneMode);
        if (ok)
        {
            var msg = "Snapshot restored successfully";
            if (backupFileName != null)
                msg += $" (backup: {backupFileName})";
            return RedirectToPage(new { msg });
        }
        return RedirectToPage(new { err = error ?? "Restore failed" });
    }

    public async Task<IActionResult> OnPostDeleteAgentAsync(int agentId)
    {
        var ok = await api.DeleteAgentAsync(agentId);
        return ok
            ? RedirectToPage(new { msg = "Agent revoked" })
            : RedirectToPage(new { err = "Failed to revoke agent" });
    }

    public async Task<IActionResult> OnPostRebuildGraphAsync()
    {
        var report = await api.RebuildConceptTagEdgesAsync();
        TempData["GraphRebuildReport"] = report?.ToString();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompactAsync(long? explicitCp = null)
    {
        var (ok, err, result) = await api.CompactAsync(explicitCp, "manual");
        if (ok && result != null)
        {
            return RedirectToPage(new { msg = $"Compacted to CP={result.CpAfter}, deleted {result.EventsDeleted} events, snapshot: {result.SnapshotFileName}" });
        }
        return RedirectToPage(new { err = $"Compaction failed: {err}" });
    }

    public async Task<IActionResult> OnPostProposeDekRotationAsync(string masterPassword)
    {
        var (ok, commitId, err) = await api.ProposeDekRotationAsync(masterPassword);
        if (ok)
        {
            TempData["DekRotationCommitId"] = commitId;
            return RedirectToPage(new { msg = "DEK rotation proposed. Confirm to begin." });
        }
        return RedirectToPage(new { err = err ?? "Failed to propose DEK rotation" });
    }

    public async Task<IActionResult> OnPostAcceptDekRotationAsync(string commitEventId, string masterPassword)
    {
        var (ok, err) = await api.AcceptDekRotationAsync(commitEventId, masterPassword);
        if (ok)
        {
            TempData.Remove("DekRotationCommitId");
            TempData["RecoveryKeyReminder"] = "true";
            return RedirectToPage(new { msg = "DEK rotation accepted. Re-encryption is in progress." });
        }
        TempData["DekRotationCommitId"] = commitEventId;
        return RedirectToPage(new { err = err ?? "Failed to accept DEK rotation" });
    }

    public async Task<IActionResult> OnPostCancelDekRotationAsync(string eventId)
    {
        var (ok, err) = await api.CancelDekRotationAsync(eventId);
        TempData.Remove("DekRotationCommitId");
        return ok
            ? RedirectToPage(new { msg = "DEK rotation cancelled." })
            : RedirectToPage(new { err = err ?? "Failed to cancel DEK rotation" });
    }

    public async Task<IActionResult> OnPostPeerAcceptDekRotationAsync(string eventId)
    {
        var (ok, err) = await api.PeerAcceptDekRotationAsync(eventId);
        return ok
            ? RedirectToPage(new { msg = "Peer DEK rotation applied successfully." })
            : RedirectToPage(new { err = err ?? "Failed to apply peer DEK rotation" });
    }

    public async Task<IActionResult> OnPostPeerRejectDekRotationAsync(string eventId)
    {
        var (ok, err) = await api.PeerRejectDekRotationAsync(eventId);
        return ok
            ? RedirectToPage(new { msg = "Peer DEK rotation rejected. This node will desync from the network." })
            : RedirectToPage(new { err = err ?? "Failed to reject peer DEK rotation" });
    }

    private async Task LoadDataAsync()
    {
        var tasks = new Task[]
        {
            api.GetIdentityAsync().ContinueWith(t => Identity = t.Result),
            api.GetWhitelistAsync().ContinueWith(t => Whitelist = t.Result),
            api.GetNodeSyncStatusAsync().ContinueWith(t => NodeSyncStatus = t.Result),
            api.GetSnapshotsAsync().ContinueWith(t => Snapshots = t.Result),
            api.GetAgentsAsync().ContinueWith(t => Agents = t.Result),
            api.GetUsersAsync().ContinueWith(t => Users = t.Result),
            api.GetConceptTagEdgeStatsAsync().ContinueWith(t => GraphEdgeStats = t.Result),
            api.GetCompactionPreviewAsync().ContinueWith(t => CompactionPreview = t.Result),
            api.GetSnapshotCheckpointsAsync().ContinueWith(t => SnapshotCheckpoints = t.Result),
            api.GetDekRotationProgressAsync().ContinueWith(t => DekRotationProgress = t.Result),
            api.GetPeerPendingDekRotationsAsync().ContinueWith(t => PeerPendingRotations = t.Result ?? new()),
        };
        await Task.WhenAll(tasks);
    }

    public static string RelativeTime(DateTime? utc)
    {
        if (utc == null) return "\u2014";
        var diff = DateTime.UtcNow - utc.Value;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}
