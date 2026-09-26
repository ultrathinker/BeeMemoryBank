using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.Mdns;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class SetupModel(ApiClient api, MdnsBrowser mdnsBrowser) : PageModel
{
    public string? ErrorMessage { get; set; }

    /// <summary>"legacy" = open-an-existing-profile panel, browser variant (opt-in, reached only via
    /// the "Open an existing profile" card outside the Windows app — never shown automatically), "" = mode-select
    /// (step 1), "form" = show form (step 2), "done" = completion (step 3)</summary>
    public string Step { get; set; } = "";

    /// <summary>"standalone" or "join" — tracks which path the user took, shown in step 3</summary>
    public string Mode { get; set; } = "standalone";

    /// <summary>Pre-filled login name: nearly everyone keeps it.</summary>
    public const string SuggestedUsername = "admin";

    /// <summary>
    /// Pre-filled name of this node: the computer's own name, which is what the user would type
    /// anyway. Empty in a container, where the machine name is a random id nobody recognises.
    /// </summary>
    public static string SuggestedNodeName =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? ""
            : Environment.MachineName;

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

        // Guard: a running Api has the destination db open, and overwriting the file then is a
        // silent no-op — Api's connection pool keeps serving the old state. Nothing may hold the
        // db while copying, and this page cannot stop the Api, so refuse and ask the user to
        // restart first (Api's first boot then opens the copied file, not a fresh empty one).
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

        return await SignInOrFinishAsync(adminUsername, password, "standalone");
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

        return await SignInOrFinishAsync(joinAdminUsername, joinPassword, "join");
    }

    /// <summary>
    /// The user has just typed the password: sign them in and open the app, instead of a "done"
    /// screen followed by the same login again. If that sign-in fails for any reason the node is
    /// still set up, so fall back to the done screen and its Continue-to-login button.
    /// </summary>
    private async Task<IActionResult> SignInOrFinishAsync(string username, string password, string mode)
    {
        try
        {
            var login = await api.LoginAsync(username, password);
            if (login.Success)
            {
                await WebSignIn.SignInAsync(HttpContext, login);
                return LocalRedirect("/Tree");
            }
        }
        catch
        {
            // Fall through to the done screen.
        }
        return RedirectToPage("/Setup", new { step = "done", mode });
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
