using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Sync.Search;

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
        // Same group + filter pattern as AdminEndpoints: the internal-key gate is applied to the
        // whole group, and each handler re-checks the session/role individually.
        var group = app.MapGroup("/api/admin/search").WithTags("Admin").RequireInternalKey();

        // GET /api/admin/search/metrics -- latency histograms + index-health numbers.
        //
        // The body is composed entirely of timings, counts, and coarse buckets surfaced from already
        // existing diagnostics (SearchMetrics from WP-18, IndexBuilder's public counters,
        // SearchIndexRuntimeState's warm-start flag). No query text, article title, or article
        // content is ever placed in the response -- see wp-18-report.md for the privacy audit.
        group.MapGet("/metrics", (
            HttpContext ctx,
            SessionService session,
            SearchMetrics metrics,
            IndexBuilder indexBuilder,
            SearchIndexRuntimeState runtimeState) =>
        {
            var gate = RequireSuperadmin(ctx, session);
            if (gate != null) return gate;

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
        group.MapGet("/embeddings-enabled", async (HttpContext ctx, SessionService session, INodeIdentityRepository nodeRepo) =>
        {
            var gate = RequireSuperadmin(ctx, session);
            if (gate != null) return gate;

            var identity = await nodeRepo.GetAsync();
            return Results.Ok(new EmbeddingsEnabledResponse(identity?.CanGenerateEmbeddings ?? false));
        });

        group.MapPut("/embeddings-enabled", async (EmbeddingsEnabledRequest req, HttpContext ctx, SessionService session, INodeIdentityRepository nodeRepo) =>
        {
            var gate = RequireSuperadmin(ctx, session);
            if (gate != null) return gate;

            await nodeRepo.SetCanGenerateEmbeddingsAsync(req.Enabled);
            return Results.Ok(new EmbeddingsEnabledResponse(req.Enabled));
        });
    }

    // 3-gate admin check shared by the admin endpoints; returns null when authorized. Kept as a
    // private copy so this endpoint file stays self-contained and matches AdminEndpoints exactly.
    private static IResult? RequireSuperadmin(HttpContext ctx, SessionService session)
    {
        if (!session.IsUnlocked)
            return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
        if (!CallerIdentity.Extract(ctx).IsSuperadmin)
            return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);
        return null;
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
