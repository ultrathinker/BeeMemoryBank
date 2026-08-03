using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class SetupModel(ApiClient api, MdnsBrowser mdnsBrowser) : PageModel
{
    public string? ErrorMessage { get; set; }

    /// <summary>"legacy" = restore-from-previous-installation panel (opt-in, reached only via the
    /// "Restore from a previous installation" link — never shown automatically), "" = mode-select
    /// (step 1), "form" = show form (step 2), "done" = completion (step 3)</summary>
    public string Step { get; set; } = "";

    /// <summary>"standalone" or "join" — tracks which path the user took, shown in step 3</summary>
    public string Mode { get; set; } = "standalone";

    public void OnGet(string? step, string? mode)
    {
        Mode = mode ?? "standalone";
        Step = step ?? "";
    }

    public async Task<IActionResult> OnPostMigrateAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            ErrorMessage = "Please specify a directory path.";
            Step = "legacy";
            return Page();
        }

        var candidate = LegacyMigrationService.ValidatePath(sourcePath);
        if (candidate == null || !candidate.IsValid)
        {
            ErrorMessage = "The specified directory is not a valid legacy BeeMemoryBank data directory.";
            Step = "legacy";
            return Page();
        }

        // Guard: if Api is already running it has the destination db open.
        // Overwriting the file while Api holds it causes a silent no-op — Api's
        // connection pool keeps serving the old in-memory state and never notices
        // the file changed. The only safe fix is to ensure nothing has the db open
        // before copying. Since coordinating a full orchestrator stop/restart is
        // out of scope here, we detect this condition honestly and ask the user to
        // restart the app before migrating (so Api's first boot opens the just-copied
        // file instead of its own auto-created empty one).
        var apiReachable = await api.GetInitStatusAsync();
        if (apiReachable != null)
        {
            ErrorMessage =
                "Migration cannot proceed while the app is fully running: the Api process " +
                "already has the destination database open, so copying the file now would " +
                "have no effect on the live process. " +
                "Please fully close BeeMemoryBank (including the tray icon / desktop app), " +
                "then reopen it and perform the migration immediately — before Api's own " +
                "auto-created empty database has any account data in it.";
            Step = "legacy";
            return Page();
        }

        try
        {
            var destPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

            await LegacyMigrationService.CopyLegacyDataAsync(sourcePath, destPath);

            return RedirectToPage("/Setup", new { step = "done", mode = "standalone" });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to copy database: {ex.Message}";
            Step = "legacy";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostStandaloneAsync(
        string adminUsername, string displayName, string password, string confirmPassword)
    {
        adminUsername = adminUsername?.Trim() ?? "";
        displayName = displayName?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(adminUsername) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "All fields are required.";
            Step = "form";
            Mode = "standalone";
            return Page();
        }

        if (password != confirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            Step = "form";
            Mode = "standalone";
            return Page();
        }

        var (ok, error) = await api.InitStandaloneAsync(adminUsername, displayName, password);
        if (!ok)
        {
            ErrorMessage = error ?? "Initialization failed.";
            Step = "form";
            Mode = "standalone";
            return Page();
        }

        return RedirectToPage("/Setup", new { step = "done", mode = "standalone" });
    }

    public async Task<IActionResult> OnPostJoinAsync(
        string joinAdminUsername, string joinDisplayName, string remoteUrl, string joinPassword)
    {
        joinAdminUsername = joinAdminUsername?.Trim() ?? "";
        joinDisplayName = joinDisplayName?.Trim() ?? "";
        remoteUrl = remoteUrl?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(joinAdminUsername) ||
            string.IsNullOrWhiteSpace(joinDisplayName) ||
            string.IsNullOrWhiteSpace(remoteUrl) ||
            string.IsNullOrWhiteSpace(joinPassword))
        {
            ErrorMessage = "All fields are required.";
            Step = "form";
            Mode = "join";
            return Page();
        }

        var (ok, error) = await api.InitJoinAsync(joinAdminUsername, joinDisplayName, remoteUrl, joinPassword);
        if (!ok)
        {
            ErrorMessage = error ?? "Join failed.";
            Step = "form";
            Mode = "join";
            return Page();
        }

        return RedirectToPage("/Setup", new { step = "done", mode = "join" });
    }

    /// <summary>
    /// JSON endpoint backing the "Found nodes on your network" list in the join step. Performs a
    /// short bounded mDNS scan and returns the discovered peers so the browser can render clickable
    /// chips that pre-fill the manual Remote Node URL field. This is purely ADDITIVE — the manual
    /// entry path is unchanged and is what actually submits <c>remoteUrl</c> in <c>OnPostJoinAsync</c>.
    /// </summary>
    public async Task<JsonResult> OnGetDiscoveredNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = await mdnsBrowser.DiscoverAsync(TimeSpan.FromSeconds(2.5), cancellationToken: cancellationToken);
        var payload = nodes.Select(n => new
        {
            nodeId = n.NodeId,
            name = n.Name,
            version = n.Version,
            https = n.Https,
            host = n.Host,
            port = n.Port,
            url = n.Url,
        });
        return new JsonResult(payload);
    }
}
