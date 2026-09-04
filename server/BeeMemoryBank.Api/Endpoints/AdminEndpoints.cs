using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // Superadmin-only in full, declared on the group. What is left inline in each handler is
        // the session-lock check, which is a different question with a different answer code.
        var group = app.MapGroup("/api/admin").WithTags("Admin")
            .RequireInternalKey().RequireSuperadmin();

        // backfill-media-links was a one-shot migration helper for an earlier schema change.
        // Disabled: leaving the rest of the /api/admin group registered so concept-tag-edge
        // stats + rebuild work. If we ever need backfill again, restore from git history.

        group.MapGet("/concept-tag-edge/stats", async (
            SessionService session, IConceptTagRepository conceptTagRepo) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var stats = await conceptTagRepo.GetEdgeStatsAsync();
            return Results.Ok(stats);
        });

        group.MapPost("/concept-tag-edge/rebuild", async (
            SessionService session, IConceptTagRepository conceptTagRepo) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var report = await conceptTagRepo.CheckAndRebuildEdgesAsync();
            return Results.Ok(report);
        }).DisableAntiforgery();

        // ─── Software updates ──────────────────────────────────────────────────
        // Detection works for every install type. Applying the update is Docker-only
        // for now: the API just writes a flag file into the data volume; a host-side
        // systemd path-unit (outside the container) picks it up and runs
        // `git reset --hard origin/master` + `docker compose up -d --build`. The API
        // never touches the Docker socket. See deploy/hetzner/bmb-update.{path,service}.

        // GET /api/admin/update/check — compare own compiled-in version to the feed.
        group.MapGet("/update/check", async (
            SessionService session, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var current = AppVersion.Current;
            var feedUrl = GetFeedUrl(config);
            if (string.IsNullOrWhiteSpace(feedUrl))
                return Results.Ok(new { current, latest = (string?)null, updateAvailable = false, error = "Update checks are not configured on this node." });
            try
            {
                var client = httpFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var json = await client.GetStringAsync(feedUrl, cts.Token);
                using var doc = JsonDocument.Parse(json);

                string? latest = null, notes = null;
                if (doc.RootElement.TryGetProperty("server", out var server))
                {
                    if (server.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        latest = v.GetString();
                    if (server.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
                        notes = n.GetString();
                }

                // Offer an update only when the feed is strictly newer (never a downgrade).
                var updateAvailable = latest != null && CompareVersions(latest, current) > 0;
                return Results.Ok(new { current, latest, updateAvailable, notes });
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                // Feed unreachable/malformed must not 500 — return a clean "can't tell" result.
                return Results.Ok(new { current, latest = (string?)null, updateAvailable = false, error = "Could not reach the update server." });
            }
        });

        // POST /api/admin/update/apply — drop the trigger flag for the host updater.
        group.MapPost("/update/apply", async (
            SessionService session, IConfiguration config) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var updatesDir = Path.Combine(ResolveDataPath(config), "updates");
            Directory.CreateDirectory(updatesDir);

            // Clear any stale result so the UI doesn't show the previous run's outcome.
            var resultFile = Path.Combine(updatesDir, "update.result");
            if (File.Exists(resultFile)) File.Delete(resultFile);

            // Write the flag atomically (temp + move) so the watcher never reads a partial file.
            var reqFile = Path.Combine(updatesDir, "update.request");
            var tmp = reqFile + ".tmp";
            var payload = JsonSerializer.Serialize(new { requestedAt = DateTime.UtcNow, current = AppVersion.Current });
            await File.WriteAllTextAsync(tmp, payload);
            File.Move(tmp, reqFile, overwrite: true);

            return Results.Ok(new { triggered = true });
        }).DisableAntiforgery();

        // GET /api/admin/update/status — async feedback: is an update running, and how did the last one go.
        group.MapGet("/update/status", (
            SessionService session, IConfiguration config) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var updatesDir = Path.Combine(ResolveDataPath(config), "updates");
            var inProgress = File.Exists(Path.Combine(updatesDir, "update.inprogress"));

            JsonElement? result = null;
            var resultFile = Path.Combine(updatesDir, "update.result");
            if (File.Exists(resultFile))
            {
                try { result = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(resultFile)); }
                catch { /* ignore a half-written result file */ }
            }

            return Results.Ok(new { inProgress, result });
        });
    }

    // No hardcoded default on purpose: the public source must not bake in a specific
    // update server (each operator configures their own feed via BMB_UPDATE_FEED_URL).
    // Unconfigured nodes simply report "not configured" instead of phoning home.
    private static string? GetFeedUrl(IConfiguration config) =>
        config["BeeMemoryBank:UpdateFeedUrl"]
        ?? Environment.GetEnvironmentVariable("BMB_UPDATE_FEED_URL");

    private static string ResolveDataPath(IConfiguration config) =>
        config["BeeMemoryBank:DataPath"]
        ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

    // Numeric major.minor.patch comparison; tolerant of "v" prefix, pre-release/build
    // suffixes, and short forms ("1.0" == "1.0.0"). Returns >0 if a is newer than b.
    private static int CompareVersions(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        for (var i = 0; i < 3; i++)
        {
            var c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int[] ParseVersion(string v)
    {
        // NOTE: pre-release/build suffixes are dropped, so "1.0.0-rc1" compares EQUAL to
        // "1.0.0" (not less, as strict semver would). Fine while the feed uses plain
        // major.minor.patch; revisit here if pre-release gating is ever needed.
        v = v.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(['-', '+']);
        if (cut >= 0) v = v[..cut];
        var parts = v.Split('.');
        var nums = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
            int.TryParse(parts[i], out nums[i]);
        return nums;
    }
}
