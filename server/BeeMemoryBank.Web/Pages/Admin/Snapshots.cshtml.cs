using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class SnapshotsModel(ApiClient api) : AdminPageModel(api)
{
    public List<SnapshotDto>? Snapshots { get; private set; }

    /// <summary>The trusted nodes: the restore dialog says how many would forget this node and how many have auto-restore switched on.</summary>
    public List<WhitelistEntryDto>? Whitelist { get; private set; }

    public async Task OnGetAsync()
    {
        await Task.WhenAll(
            Api.GetSnapshotsAsync().ContinueWith(t => Snapshots = t.Result),
            Api.GetWhitelistAsync().ContinueWith(t => Whitelist = t.Result));
    }

    public async Task<IActionResult> OnPostCreateSnapshotAsync()
    {
        var snap = await Api.CreateSnapshotAsync();
        return snap != null
            ? Back(msg: "Snapshot created")
            : Back(err: "Failed to create snapshot");
    }

    public async Task<IActionResult> OnPostDeleteSnapshotAsync(string fileName)
    {
        var ok = await Api.DeleteSnapshotAsync(fileName);
        return ok
            ? Back(msg: "Snapshot deleted")
            : Back(err: "Failed to delete snapshot");
    }

    public async Task<IActionResult> OnPostUploadSnapshotAsync(IFormFile file)
    {
        var result = await Api.UploadSnapshotAsync(file);
        if (result == null) return BadRequest("Upload failed");
        return new JsonResult(result);
    }

    public async Task<IActionResult> OnPostInitiateNetworkRestoreAsync(string fileName, string? masterPassword)
    {
        // Restoring every node is the most destructive thing this page offers: the password is asked for
        // again, as it is for a restore of this node alone.
        if (string.IsNullOrEmpty(masterPassword))
            return new JsonResult(new { error = "Enter the master password to confirm the restore." }) { StatusCode = 400 };

        var (ok, eventId, error) = await Api.InitiateNetworkRestoreAsync(fileName, masterPassword);
        if (!ok) return new JsonResult(new { error }) { StatusCode = 400 };
        return new JsonResult(new { eventId });
    }

    public async Task<IActionResult> OnGetDownloadSnapshotAsync(string fileName)
    {
        var result = await Api.DownloadSnapshotAsync(fileName);
        if (result == null || !result.IsSuccessStatusCode)
            return Back(err: "Failed to download snapshot");

        var stream = await result.Content.ReadAsStreamAsync();
        return new FileStreamResult(new DisposingStreamWrapper(stream, result), "application/gzip") { FileDownloadName = fileName };
    }

    /// <summary>
    /// Restores this node only: it becomes a new node (the network restore has its own handler). On success the
    /// user lands on the sign-in page, which says what happened — the vault is locked after a restore, and the
    /// node is a new one that the other devices have to be joined to again.
    /// </summary>
    public async Task<IActionResult> OnPostRestoreSnapshotAsync(
        string fileName, string? masterPassword, bool createBackupFirst = true, string? mode = null)
    {
        // Nothing but a restore of this node is served here, and a value nobody defined is refused rather than read as the default.
        if (!RestoreModes.IsStandalone(mode))
            return Back(err: "Unknown restore mode. Restoring the whole network is started from the Restore dialog with that option chosen.");
        if (string.IsNullOrEmpty(masterPassword))
            return Back(err: "Enter the master password to confirm the restore.");

        var (ok, error, _) = await Api.RestoreSnapshotAsync(fileName, masterPassword, createBackupFirst, RestoreModes.Standalone);
        if (ok)
            return RedirectToPage("/Login", new { restored = RestoreModes.Standalone });
        return Back(err: error ?? "Restore failed");
    }
}
