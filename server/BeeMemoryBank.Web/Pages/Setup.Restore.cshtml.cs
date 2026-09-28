using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages;

// "Restore from a blind node" and restore from a blind node's backup (BMB-43, plan 6.7, 6.8). The Api
// does the work in the background; these handlers start it and the panel polls its progress.
public partial class SetupModel
{
    /// <summary>A backup location recognised by "Open an existing profile", pre-filled in the restore form.</summary>
    [BindProperty(SupportsGet = true, Name = "backup")]
    public string? RestoreBackupPath { get; set; }

    public async Task<IActionResult> OnPostRestoreBlindAsync(string? address, string code, string password, string adminUsername, string displayName)
    {
        var (ok, error) = await api.StartRestoreFromBlindAsync(address, code, password, adminUsername, displayName);
        if (ok) return RedirectToPage("/Setup", new { step = "restoring" });
        ErrorMessage = error ?? "The restore could not be started.";
        Step = "restore";
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreBackupAsync(string backupPath, string password, string adminUsername, string displayName)
    {
        var (ok, error) = await api.StartRestoreFromBackupAsync(backupPath, password, adminUsername, displayName);
        if (ok) return RedirectToPage("/Setup", new { step = "restoring" });
        ErrorMessage = error ?? "The restore could not be started.";
        RestoreBackupPath = backupPath;
        Step = "restore";
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreContinueAsync(string boxes)
    {
        var (ok, error) = await api.ContinueRestoreAsync(boxes);
        if (!ok) ErrorMessage = error;
        return RedirectToPage("/Setup", new { step = "restoring" });
    }

    /// <summary>How many devices the wizard just confirmed (shown on the result panel).</summary>
    [BindProperty(SupportsGet = true, Name = "confirmed")]
    public int? ConfirmedPeers { get; set; }

    public async Task<IActionResult> OnPostRestoreConfirmPeersAsync(List<Guid> nodeIds, string password)
    {
        var (ok, error) = await api.ConfirmRestoredPeersWithPasswordAsync(password, nodeIds);
        if (!ok) return RedirectToPage("/Setup", new { step = "restoring", confirmError = error ?? "The devices could not be confirmed." });
        return RedirectToPage("/Setup", new { step = "restoring", confirmed = nodeIds.Count });
    }

    [BindProperty(SupportsGet = true, Name = "confirmError")]
    public string? ConfirmPeersError { get; set; }

    public async Task<IActionResult> OnPostRestoreCancelAsync()
    {
        await api.CancelRestoreAsync();
        return RedirectToPage("/Setup", new { step = "restoring" });
    }

    /// <summary>
    /// A folder holding a blind node's backup: a recovery set next to a database copy, or a restic
    /// repository with <c>&lt;repo&gt;.recovery-set.json</c> beside it.
    /// </summary>
    /// <param name="fileRecognizers">Recognizers of one-file backups (<see cref="IBackupFileRecognizer"/>).</param>
    public static bool LooksLikeBlindBackup(string path, IEnumerable<BeeMemoryBank.Web.Services.IBackupFileRecognizer>? fileRecognizers = null)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            if (System.IO.File.Exists(full) && fileRecognizers?.Any(r => r.Recognizes(full)) == true) return true;
            if (!Directory.Exists(full)) return false;
            if (Directory.EnumerateFiles(full, "*.recovery-set.json").Any()) return true;
            return System.IO.File.Exists(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".recovery-set.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
