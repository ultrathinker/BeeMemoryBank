using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize(Roles = "superadmin")]
public class AdminModel(ApiClient api) : PageModel
{
    public NodeIdentityDto? Identity { get; private set; }
    public List<WhitelistEntryDto>? Whitelist { get; private set; }
    public Dictionary<Guid, DateTime>? NodeSyncStatus { get; private set; }
    public List<SnapshotDto>? Snapshots { get; private set; }
    public List<AgentDto>? Agents { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? NewAgentKey { get; private set; }
    public bool DeployEnabled { get; private set; }
    public string? DeployOutput { get; private set; }

    public async Task OnGetAsync(string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnPostCreateSnapshotAsync()
    {
        var snap = await api.CreateSnapshotAsync();
        return snap != null
            ? RedirectToPage(new { msg = $"Snapshot created: {snap.FileName}" })
            : RedirectToPage(new { err = "Failed to create snapshot" });
    }

    public async Task<IActionResult> OnPostDeleteSnapshotAsync(string fileName)
    {
        var ok = await api.DeleteSnapshotAsync(fileName);
        return ok
            ? RedirectToPage(new { msg = "Snapshot deleted" })
            : RedirectToPage(new { err = "Failed to delete snapshot" });
    }

    public async Task<IActionResult> OnGetDownloadSnapshotAsync(string fileName)
    {
        var resp = await api.DownloadSnapshotAsync(fileName);
        if (resp == null)
            return RedirectToPage(new { err = "Failed to download snapshot" });

        if (!resp.IsSuccessStatusCode)
            return RedirectToPage(new { err = "Failed to download snapshot" });

        var stream = await resp.Content.ReadAsStreamAsync();
        return new FileStreamResult(stream, "application/gzip") { FileDownloadName = fileName };
    }

    public async Task<IActionResult> OnPostRestoreSnapshotAsync(
        string fileName, string masterPassword, bool createBackupFirst = true)
    {
        var (ok, error, backupFileName) = await api.RestoreSnapshotAsync(
            fileName, masterPassword, createBackupFirst);
        if (ok)
        {
            var msg = "Snapshot restored successfully";
            if (backupFileName != null)
                msg += $" (backup: {backupFileName})";
            return RedirectToPage(new { msg });
        }
        return RedirectToPage(new { err = error ?? "Restore failed" });
    }

    public async Task<IActionResult> OnPostCreateAgentAsync(string agentName, string? agentDescription)
    {
        var result = await api.CreateAgentAsync(agentName, agentDescription);
        if (result == null)
            return RedirectToPage(new { err = "Failed to create agent" });

        // Show the key once
        SuccessMessage = $"Agent '{result.Name}' created";
        NewAgentKey = result.ApiKey;
        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAgentAsync(int agentId)
    {
        var ok = await api.DeleteAgentAsync(agentId);
        return ok
            ? RedirectToPage(new { msg = "Agent deleted" })
            : RedirectToPage(new { err = "Failed to delete agent" });
    }

    public async Task<IActionResult> OnPostRevokeNodeAsync(Guid nodeId)
    {
        var ok = await api.RevokeNodeAsync(nodeId);
        return ok
            ? RedirectToPage(new { msg = "Node revoked" })
            : RedirectToPage(new { err = "Failed to revoke node" });
    }

    public async Task<IActionResult> OnPostAddNodeAsync(
        Guid nodeId, string displayName, string ed25519PublicKeyB64, string? apiAddress)
    {
        var ok = await api.AddWhitelistEntryAsync(nodeId, displayName, ed25519PublicKeyB64, apiAddress);
        return ok
            ? RedirectToPage(new { msg = $"Node '{displayName}' added" })
            : RedirectToPage(new { err = "Failed to add node" });
    }

    public async Task<IActionResult> OnPostChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var (ok, error) = await api.ChangeNodeAddressAsync(nodeId, newApiAddress, password);
        return ok
            ? RedirectToPage(new { msg = "Node URL updated" })
            : RedirectToPage(new { err = error ?? "Failed to change URL" });
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(string oldPassword, string newPassword)
    {
        var ok = await api.ChangePasswordAsync(oldPassword, newPassword);
        return ok
            ? RedirectToPage(new { msg = "Password changed" })
            : RedirectToPage(new { err = "Failed to change password" });
    }

    public async Task<IActionResult> OnPostDeployAsync(string password)
    {
        var (ok, _) = await api.DeployAsync(password);
        if (!ok)
            return RedirectToPage(new { err = "Deploy failed to start" });

        return Redirect("/maintenance.html");
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
            api.IsDeployEnabledAsync().ContinueWith(t => DeployEnabled = t.Result),
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
