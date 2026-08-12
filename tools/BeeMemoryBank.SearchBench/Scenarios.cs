using System.Diagnostics;

namespace BeeMemoryBank.SearchBench;

/// <summary>
/// The four benchmark scenarios. Each produces a <see cref="ScenarioResult"/> with the same shape
/// of stats so the baseline JSON files are directly comparable across scenarios and corpus sizes.
/// </summary>
internal static class Scenarios
{
    // ── Query mixes ──────────────────────────────────────────────────────────────
    // Chosen from tools/BeeMemoryBank.SeedGen's topic/tag/body word sources:
    //  - Title/metadata search hits folder names + titles + concept tags. Titles are always
    //    "<TopicWord> ..." and folders are built from TopicWords, so a topic word matches a lot.
    //  - Body/content search decrypts every active body (linear scan in SearchService) and does a
    //    substring check, so a common English word ("the") matches the English-locale half of the
    //    corpus while still forcing a full scan of ALL bodies.
    //  - Semantic search projects the query through the ONNX model and returns nearest articles by
    //    embedding — only meaningful once the embedding backfill has populated projections.

    public sealed record QuerySpec(string Text, string Expectation);

    public static readonly IReadOnlyList<QuerySpec> TitleQueries =
    [
        // Selectivity was confirmed empirically against a seeded corpus (locale ru,en). Note: under
        // --locale ru,en the folder tree is built from the RU topic pool (e.g. /Инженерия), so pure
        // English folder words ("Engineering", "Runbooks", …) match nothing; titles and tags are the
        // reliable English-text surface. "Review" is the clear frequent winner because the metadata
        // match is prefix-like, so it also catches Reviews/Reviewed. Counts below are at 150 articles
        // and scale roughly linearly with corpus size.
        new("Review", "frequent"),        // ~43 @ 150 — prefix-matches Review/Reviews/Reviewed
        new("Инженерия", "frequent"),     // ~15 @ 150 — Russian folder tree (folder-heavy)
        new("Onboarding", "medium"),      // ~5  @ 150
        new("Performance", "medium"),     // ~4  @ 150
        new("Deployments", "medium"),     // ~3  @ 150
        new("Strategy", "rare"),          // ~2  @ 150
        new("Architecture", "rare")       // ~2  @ 150
    ];

    public static readonly IReadOnlyList<QuerySpec> ContentQueries =
    [
        // Body/content search decrypts EVERY active body (linear scan in SearchService) and does an
        // OrdinalIgnoreCase Contains — the scan cost is corpus-proportional and independent of how
        // many bodies match, so the query only changes the result count (response size), not the
        // work. Confirmed match counts at 150 articles shown for the selectivity indicator.
        new("the", "frequent"),            // ~71 @ 150 — in most English bodies
        new("system", "frequent"),         // ~20 @ 150
        new("data", "medium"),             // ~12 @ 150
        new("performance", "medium"),      // ~15 @ 150
        new("infrastructure", "rare")      // ~2  @ 150
    ];

    public static readonly IReadOnlyList<QuerySpec> SemanticQueries =
    [
        new("incident response runbook", "frequent"),
        new("quarterly performance review", "frequent"),
        new("onboarding new engineer", "frequent"),
        new("security audit findings", "rare"),
        new("capacity planning and scaling", "rare")
    ];

    /// <summary>Runs the title/metadata search scenario (<c>GET /api/search?q=...</c>).</summary>
    public static async Task<ScenarioResult> TitleAsync(BenchClient client, Options opt, string corpusLabel, CancellationToken ct)
    {
        return await ClosedLoopAsync(client, opt, corpusLabel, "title", TitleQueries, content: false, ct);
    }

    /// <summary>Runs the body/content search scenario (<c>GET /api/search?q=...&amp;content=true</c>) — the linear scan path.</summary>
    public static async Task<ScenarioResult> ContentAsync(BenchClient client, Options opt, string corpusLabel, CancellationToken ct)
    {
        return await ClosedLoopAsync(client, opt, corpusLabel, "content", ContentQueries, content: true, ct);
    }

    /// <summary>Runs the semantic search scenario (<c>POST /api/search/semantic</c>), after waiting for embeddings.</summary>
    public static async Task<ScenarioResult> SemanticAsync(BenchClient client, Options opt, string corpusLabel,
        Func<TextWriter, CancellationToken, Task> awaitEmbeddings, TextWriter progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        await progress.WriteLineAsync("  semantic: waiting for embedding backfill to settle...");
        await awaitEmbeddings(progress, ct);

        return await ClosedLoopAsync(client, opt, corpusLabel, "semantic", SemanticQueries,
            content: false, ct, semantic: true, startedAt: started);
    }

    /// <summary>Runs the mixed concurrent-load scenario (N clients, fixed duration, random query mix).</summary>
    public static async Task<ScenarioResult> MixedAsync(BenchClient client, Options opt, string corpusLabel, TextWriter progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var duration = TimeSpan.FromSeconds(opt.MixedDurationSeconds);
        var clients = opt.MixedClients;
        await progress.WriteLineAsync($"  mixed: {clients} concurrent clients for {duration.TotalSeconds:0}s");

        using var mixedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        mixedCts.CancelAfter(duration);

        var allLatencies = new List<double>();
        var allLock = new object();
        long total = 0, errors = 0, success = 0;
        // Weighted mix: title 50%, content 30%, semantic 20%.
        var plan = BuildMixedPlan();
        var rng = new Random(opt.Seed);

        var workers = new Task[clients];
        for (int i = 0; i < clients; i++)
        {
            int workerId = i;
            workers[workerId] = Task.Run(async () =>
            {
                while (!mixedCts.IsCancellationRequested)
                {
                    var (query, content, semantic) = PickMixed(plan, rng);
                    var (ok, _, latencyMs, _, _) = semantic
                        ? await client.SendSemanticAsync(query, topK: 20, mixedCts.Token)
                        : await client.SendSearchAsync(query, content, mixedCts.Token);

                    lock (allLock)
                    {
                        total++;
                        if (ok) success++; else errors++;
                        allLatencies.Add(latencyMs);
                    }

                    // Think time between requests (closed-loop with a human-ish gap).
                    int thinkMs = 25 + rng.Next(76); // 25..100ms
                    try { await Task.Delay(thinkMs, mixedCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }, mixedCts.Token);
        }

        var sw = Stopwatch.StartNew();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { /* expected when duration elapses */ }
        sw.Stop();
        var ended = DateTime.UtcNow;

        var latencies = allLatencies.ToArray();
        var (p50, p95, p99, mean, min, max) = Stats.Summary(latencies);
        double measuredSecs = sw.Elapsed.TotalSeconds;
        double throughput = measuredSecs > 0 ? total / measuredSecs : 0;

        return new ScenarioResult
        {
            Scenario = "mixed",
            CorpusSizeLabel = corpusLabel,
            StartedAtUtc = started,
            EndedAtUtc = ended,
            DurationSeconds = measuredSecs,
            TotalRequests = total,
            SuccessCount = success,
            ErrorCount = errors,
            Concurrency = clients,
            LatencyP50Ms = p50,
            LatencyP95Ms = p95,
            LatencyP99Ms = p99,
            LatencyMeanMs = mean,
            LatencyMinMs = min,
            LatencyMaxMs = max,
            ThroughputReqPerSec = throughput,
            Note = errors > 0 ? $"{errors} non-2xx responses (timeouts/5xx under load)" : null
        };
    }

    // ── Closed-loop single-client driver (used by title/content/semantic) ──────────

    private static async Task<ScenarioResult> ClosedLoopAsync(
        BenchClient client, Options opt, string corpusLabel, string scenarioName,
        IReadOnlyList<QuerySpec> queries, bool content, CancellationToken ct,
        bool semantic = false, DateTime? startedAt = null)
    {
        var started = startedAt ?? DateTime.UtcNow;
        var perQuery = new List<QueryBreakdown>();
        var allLatencies = new List<double>();
        var sw = Stopwatch.StartNew();

        foreach (var q in queries)
        {
            // Warmup (unmeasured) — primes SQLite page cache and JIT for this query.
            for (int w = 0; w < opt.Warmup; w++)
            {
                if (ct.IsCancellationRequested) break;
                if (semantic) await client.SendSemanticAsync(q.Text, topK: 20, ct);
                else await client.SendSearchAsync(q.Text, content, ct);
            }

            var samples = new double[opt.Runs];
            long lastCount = -1;
            for (int i = 0; i < opt.Runs; i++)
            {
                if (ct.IsCancellationRequested) break;
                var (ok, _, latencyMs, count, _) = semantic
                    ? await client.SendSemanticAsync(q.Text, topK: 20, ct)
                    : await client.SendSearchAsync(q.Text, content, ct);
                samples[i] = latencyMs;
                if (ok && count.HasValue) lastCount = count.Value;
            }

            var (p50, p95, p99, mean, min, max) = Stats.Summary(samples);
            perQuery.Add(new QueryBreakdown
            {
                Query = q.Text,
                Expectation = q.Expectation,
                Samples = samples.Length,
                P50Ms = p50,
                P95Ms = p95,
                P99Ms = p99,
                MeanMs = mean,
                MinMs = min,
                MaxMs = max,
                ResultCount = lastCount
            });
            allLatencies.AddRange(samples);
        }

        sw.Stop();
        var ended = DateTime.UtcNow;
        var all = Stats.Summary(allLatencies.ToArray());

        return new ScenarioResult
        {
            Scenario = scenarioName,
            CorpusSizeLabel = corpusLabel,
            StartedAtUtc = started,
            EndedAtUtc = ended,
            DurationSeconds = sw.Elapsed.TotalSeconds,
            TotalRequests = allLatencies.Count,
            SuccessCount = allLatencies.Count,
            ErrorCount = 0,
            Concurrency = 1,
            LatencyP50Ms = all.p50,
            LatencyP95Ms = all.p95,
            LatencyP99Ms = all.p99,
            LatencyMeanMs = all.mean,
            LatencyMinMs = all.min,
            LatencyMaxMs = all.max,
            ThroughputReqPerSec = sw.Elapsed.TotalSeconds > 0 ? allLatencies.Count / sw.Elapsed.TotalSeconds : 0,
            PerQuery = perQuery
        };
    }

    // ── Mixed-load helpers ──────────────────────────────────────────────────────

    private sealed record MixedBucket(QuerySpec Spec, bool Content, bool Semantic, double Weight);

    private static List<MixedBucket> BuildMixedPlan()
    {
        var plan = new List<MixedBucket>();
        // title 50%
        foreach (var q in TitleQueries) plan.Add(new MixedBucket(q, false, false, 50.0 / TitleQueries.Count));
        // content 30%
        foreach (var q in ContentQueries) plan.Add(new MixedBucket(q, true, false, 30.0 / ContentQueries.Count));
        // semantic 20%
        foreach (var q in SemanticQueries) plan.Add(new MixedBucket(q, false, true, 20.0 / SemanticQueries.Count));
        return plan;
    }

    private static (string query, bool content, bool semantic) PickMixed(List<MixedBucket> plan, Random rng)
    {
        double total = 0;
        foreach (var b in plan) total += b.Weight;
        double r = rng.NextDouble() * total;
        double acc = 0;
        foreach (var b in plan)
        {
            acc += b.Weight;
            if (r <= acc)
                return (b.Spec.Text, b.Content, b.Semantic);
        }
        var last = plan[^1];
        return (last.Spec.Text, last.Content, last.Semantic);
    }
}
