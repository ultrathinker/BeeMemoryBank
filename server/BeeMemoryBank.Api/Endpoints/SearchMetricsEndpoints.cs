using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// WP-18: admin-only endpoint exposing the search subsystem's process-wide metrics and index-health
/// signals so the Admin page can render a "Search" diagnostics section. Mirrors the gating every
/// other admin endpoint uses (internal key via the route-group filter, plus an unlocked session and
/// a superadmin role checked in-handler as defense-in-depth) so even coarse metrics never reach an
/// unauthenticated or non-superadmin caller.
/// </summary>
public static class SearchMetricsEndpoints
{
    public static void MapSearchMetricsEndpoints(this WebApplication app)
    {
        // Same group + filter pattern as AdminEndpoints: internal-key and superadmin gates on the
        // whole group; each handler still checks the session lock itself, since "locked" is a
        // different refusal from "wrong role".
        var group = app.MapGroup("/api/admin/search").WithTags("Admin")
            .RequireInternalKey().RequireSuperadmin();

        // GET /api/admin/search/metrics -- latency histograms + index-health numbers.
        //
        // The body is composed entirely of timings, counts, and coarse buckets surfaced from already
        // existing diagnostics (SearchMetrics from WP-18, IndexBuilder's public counters,
        // SearchIndexRuntimeState's warm-start flag). No query text, article title, or article
        // content is ever placed in the response -- see wp-18-report.md for the privacy audit.
        group.MapGet("/metrics", (
            SessionService session,
            SearchMetrics metrics,
            IndexBuilder indexBuilder,
            SearchIndexRuntimeState runtimeState) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            SearchMetricsSnapshot snapshot = metrics.GetSnapshot();

            // Render the per-type metrics as a stable list (order: metadata, content, semantic, then
            // any unexpected label last) so the UI table doesn't reshuffle between requests.
            var order = new[] { SearchMetrics.MetadataSearch, SearchMetrics.ContentSearch, SearchMetrics.SemanticSearch };
            var seen = new HashSet<string>(order, StringComparer.Ordinal);
            var latency = new List<SearchTypeMetricsDto>(snapshot.BySearchType.Count);
            foreach (var label in order)
            {
                if (snapshot.BySearchType.TryGetValue(label, out var m))
                    latency.Add(SearchTypeMetricsDto.From(label, m));
            }
            foreach (var (label, m) in snapshot.BySearchType)
            {
                if (seen.Add(label))
                    latency.Add(SearchTypeMetricsDto.From(label, m));
            }

            var indexHealth = new SearchIndexHealthDto(
                SealCount: indexBuilder.SealCount,
                MergeCount: indexBuilder.MergeCount,
                SealedSegmentCount: indexBuilder.SealedSegmentCount,
                HotBufferCount: indexBuilder.HotBufferCount,
                WarmStartAttempted: runtimeState.IsWarmStartAttempted);

            return Results.Ok(new SearchMetricsResponse(latency, indexHealth));
        });

        // GET/PUT /api/admin/search/embeddings-enabled -- the missing self-toggle for
        // tbl_node_identity.can_generate_embeddings. Found 2026-08-12: this flag could previously
        // only be flipped by hand-editing the database directly -- no CLI command, Admin UI control,
        // or REST endpoint touched it after node init (PUT /api/whitelist/{nodeId} looks similar but
        // edits tbl_whitelist, i.e. what this node believes about OTHER nodes, never its own row).
        group.MapGet("/embeddings-enabled", async (SessionService session, INodeIdentityRepository nodeRepo) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var identity = await nodeRepo.GetAsync();
            return Results.Ok(new EmbeddingsEnabledResponse(identity?.CanGenerateEmbeddings ?? false));
        });

        group.MapPut("/embeddings-enabled", async (EmbeddingsEnabledRequest req, SessionService session, INodeIdentityRepository nodeRepo) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            await nodeRepo.SetCanGenerateEmbeddingsAsync(req.Enabled);
            return Results.Ok(new EmbeddingsEnabledResponse(req.Enabled));
        });

        // POST /api/admin/search/embeddings/backfill -- one-shot catch-up for nodes that just had
        // embeddings turned on (or an index rebuild pending) after a mass import: at the default
        // 5-min tick / 50-item batch, hundreds of pending articles can take hours of wall-clock
        // time to drain even though the actual compute is a fraction of that. This drains both
        // queues back-to-back with no inter-batch delay, in the background, and returns
        // immediately -- a full drain of a large backlog is not something an HTTP request should
        // block on. Progress isn't polled here; the existing GET /metrics index-health numbers and
        // server logs ("Processed embeddings:" / "Indexed articles:") are the way to watch it.
        group.MapPost("/embeddings/backfill", (
            SessionService session,
            PendingEmbeddingProcessor embeddingProcessor,
            PendingIndexProcessor indexProcessor,
            IHostApplicationLifetime lifetime,
            ILogger<PendingEmbeddingProcessor> logger) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            // Use the host's own shutdown token, not CancellationToken.None -- otherwise this
            // background drain ignores app shutdown entirely, delaying it and risking
            // ObjectDisposedException from DB pools torn down mid-batch.
            var shutdownToken = lifetime.ApplicationStopping;
            _ = Task.Run(async () =>
            {
                try
                {
                    var embedded = await embeddingProcessor.DrainAllPendingAsync(shutdownToken);
                    var indexed = await indexProcessor.DrainAllPendingAsync(shutdownToken);
                    logger.LogInformation("Manual backfill complete: {Embedded} embeddings, {Indexed} index entries", embedded, indexed);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Manual backfill failed");
                }
            });

            return Results.Accepted(value: new { message = "Backfill started in the background; watch server logs or GET /metrics for progress." });
        });
    }

}

/// <summary>The whole response: per-search-type latency rollups + the index-health snapshot.</summary>
public sealed record SearchMetricsResponse(
    IReadOnlyList<SearchTypeMetricsDto> Latency,
    SearchIndexHealthDto IndexHealth);

/// <summary>One search-type row in the admin table. Rounded latency keeps the JSON tidy.</summary>
public sealed record SearchTypeMetricsDto(
    string SearchType,
    long Count,
    double P50Ms,
    double P95Ms,
    long BucketZero,
    long BucketOneToTen,
    long BucketElevenToHundred,
    long BucketOverHundred)
{
    public static SearchTypeMetricsDto From(string label, SearchTypeMetrics m) => new(
        label,
        m.Count,
        Math.Round(m.P50Ms, 3),
        Math.Round(m.P95Ms, 3),
        m.Buckets.Zero,
        m.Buckets.OneToTen,
        m.Buckets.ElevenToHundred,
        m.Buckets.OverHundred);
}

/// <summary>
/// Cross-cutting diagnostics for the in-memory encrypted search index (WP-08..13). Every field here
/// is read straight off already-public properties on <see cref="IndexBuilder"/> / the warm-start
/// flag on <see cref="SearchIndexRuntimeState"/>; this WP adds no new counters to either of those.
/// </summary>
public sealed record SearchIndexHealthDto(
    int SealCount,
    int MergeCount,
    int SealedSegmentCount,
    int HotBufferCount,
    bool WarmStartAttempted);

public sealed record EmbeddingsEnabledRequest(bool Enabled);
public sealed record EmbeddingsEnabledResponse(bool Enabled);
