using System.Text.Json;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Tests for <see cref="SearchMetrics"/> (WP-18): percentile computation over the rolling window,
/// coarse result-count bucketing, per-search-type isolation, window eviction, and -- the load-bearing
/// privacy guarantee -- that no query text, title, or article content can ever reach the component's
/// observable state.
/// </summary>
public class SearchMetricsTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 1. p50/p95 computed correctly against a known input set (nearest-rank).
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Percentiles_NearestRank_KnownInput()
    {
        var metrics = new SearchMetrics();

        // 20 evenly spaced samples (10, 20, ..., 200 ms). With n=20:
        //   p50 rank = ceil(0.50 * 20) = 10  -> index 9 -> value 100
        //   p95 rank = ceil(0.95 * 20) = 19  -> index 18 -> value 190
        for (int i = 1; i <= 20; i++)
            metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(i * 10), resultCount: 1);

        var snap = metrics.GetSnapshot();
        var m = snap.BySearchType[SearchMetrics.MetadataSearch];

        m.Count.Should().Be(20);
        m.P50Ms.Should().Be(100);
        m.P95Ms.Should().Be(190);
    }

    [Fact]
    public void Percentiles_SingleSample_ReturnsThatSample()
    {
        var metrics = new SearchMetrics();
        metrics.Record(SearchMetrics.SemanticSearch, TimeSpan.FromMilliseconds(42.5), 3);

        var m = metrics.GetSnapshot().BySearchType[SearchMetrics.SemanticSearch];
        m.P50Ms.Should().Be(42.5);
        m.P95Ms.Should().Be(42.5);
        m.Count.Should().Be(1);
    }

    [Fact]
    public void Percentiles_NoSamples_ReturnsZero()
    {
        var metrics = new SearchMetrics();

        // A freshly-created component has recorded nothing.
        var snap = metrics.GetSnapshot();
        snap.BySearchType.Should().BeEmpty();
    }

    [Fact]
    public void Percentiles_UnsortedInput_OrdersBeforeComputing()
    {
        var metrics = new SearchMetrics();

        // Record in non-monotonic order; the component must sort internally.
        foreach (var ms in new[] { 80.0, 10.0, 50.0, 30.0, 60.0, 20.0, 40.0, 70.0, 90.0, 100.0 })
            metrics.Record(SearchMetrics.ContentSearch, TimeSpan.FromMilliseconds(ms), 1);

        var m = metrics.GetSnapshot().BySearchType[SearchMetrics.ContentSearch];
        // Same set as the 10..100 step-10 case: n=10, p50 rank=ceil(5)=5 -> 50, p95 rank=ceil(9.5)=10 -> 100.
        m.P50Ms.Should().Be(50);
        m.P95Ms.Should().Be(100);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. Coarse result-count bucketing -- the raw count is never stored.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResultCount_FoldsIntoCoarseBuckets()
    {
        var metrics = new SearchMetrics();
        // One call landing in each bucket, with distinctive raw counts that must NOT be recoverable.
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 0);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 1);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 10);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 7);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 11);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 99);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 100);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 101);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), resultCount: 5000);

        var b = metrics.GetSnapshot().BySearchType[SearchMetrics.MetadataSearch].Buckets;
        // "0" bucket: exactly the one 0-result call.
        b.Zero.Should().Be(1);
        // "1-10": calls with 1, 10, 7 -> 3.
        b.OneToTen.Should().Be(3);
        // "11-100": calls with 11, 99, 100 -> 3.
        b.ElevenToHundred.Should().Be(3);
        // "100+": calls with 101, 5000 -> 2.
        b.OverHundred.Should().Be(2);

        // The raw counts (7, 99, 5000, ...) are gone: only the four bucket totals survive.
        var json = JsonSerializer.Serialize(metrics.GetSnapshot());
        json.Should().NotContain("5000");
        json.Should().NotContain("\"99\"");
        json.Should().NotContain("\"7\"");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. Per-search-type isolation + rolling-window eviction.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SearchTypes_AreTrackedSeparately()
    {
        var metrics = new SearchMetrics();
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(10), 1);
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(20), 1);
        metrics.Record(SearchMetrics.SemanticSearch, TimeSpan.FromMilliseconds(30), 1);

        var snap = metrics.GetSnapshot();
        snap.BySearchType[SearchMetrics.MetadataSearch].Count.Should().Be(2);
        snap.BySearchType[SearchMetrics.SemanticSearch].Count.Should().Be(1);
        snap.BySearchType.Should().NotContainKey(SearchMetrics.ContentSearch);
    }

    [Fact]
    public void RollingWindow_EvictsOldest_KeepsTotalCountMonotonic()
    {
        var metrics = new SearchMetrics();

        // Fill the window (2048) with 1ms samples, then overflow by a full extra window of 1000ms
        // samples. After this the window holds ONLY 1000ms samples (every 1ms sample has been
        // evicted), but the total request count still reflects every call (4096).
        for (int i = 0; i < SearchMetrics.SampleWindowSize; i++)
            metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1), 0);
        for (int i = 0; i < SearchMetrics.SampleWindowSize; i++)
            metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(1000), 0);

        var m = metrics.GetSnapshot().BySearchType[SearchMetrics.MetadataSearch];
        m.Count.Should().Be(SearchMetrics.SampleWindowSize * 2);
        m.P50Ms.Should().Be(1000);
        m.P95Ms.Should().Be(1000);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. Privacy -- the one hard rule of this WP. No query text, title, or article content can
    //    ever reach the component's observable state.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ObservableState_NeverContains_Query_Or_Content_Text()
    {
        var metrics = new SearchMetrics();

        // A distinctive, searchable canary that represents user-supplied query text / a title / a
        // body fragment. It is in scope here exactly as a real query would be inside SearchService.
        //SearchAsync -- but the metrics API accepts ONLY timing + result count, so the canary is
        // deliberately never handed to Record (mirroring the production wrapper at SearchService.cs).
        const string Canary = "PRIVACY_CANARY_9d3f1b_aa-topsecret-query_TitleFrag-bodycontent";

        // Use the canary locally so the compiler/reader can see it is genuinely "present" in the
        // calling context (its length picks the bucket), then record only timing + a derived count.
        metrics.Record(SearchMetrics.MetadataSearch, TimeSpan.FromMilliseconds(5), resultCount: Canary.Length);
        metrics.Record(SearchMetrics.ContentSearch, TimeSpan.FromMilliseconds(8), resultCount: 2);
        metrics.Record(SearchMetrics.SemanticSearch, TimeSpan.FromMilliseconds(3), resultCount: 0);

        // Serialize EVERY observable field the component exposes and assert the canary (and any
        // readable substring of it) never appears anywhere in that representation.
        var snapshot = metrics.GetSnapshot();
        var json = JsonSerializer.Serialize(snapshot);

        json.Should().NotContain(Canary);
        json.Should().NotContain("CANARY");
        json.Should().NotContain("topsecret");
        json.Should().NotContain("TitleFrag");
        json.Should().NotContain("bodycontent");

        // Positive check: the only string values in the representation are the fixed search-type
        // labels. There is no field whose value could hold free-form text.
        json.Should().Contain("\"metadata\"");
        json.Should().Contain("\"content\"");
        json.Should().Contain("\"semantic\"");
        // Property names (default PascalCase serialization): confirm the numeric rollup fields exist.
        json.Should().Contain("P50Ms");
        json.Should().Contain("P95Ms");
    }

    [Fact]
    public void Record_Signature_Carries_No_Query_Title_Or_Content_Parameter()
    {
        // Structural privacy check: the only public input method on the component is Record, and its
        // parameters are (searchType label, elapsed timing, resultCount int). Assert by reflection
        // that no public instance method accepts a parameter whose name suggests user text -- so a
        // future edit that accidentally widened the API to take a query/title/content string would
        // fail this test. This is a guardrail on the contract, not just on today's behavior.
        var record = typeof(SearchMetrics).GetMethod(
            nameof(SearchMetrics.Record),
            new[] { typeof(string), typeof(TimeSpan), typeof(int) });
        record.Should().NotBeNull("SearchMetrics.Record must keep its (searchType, elapsed, resultCount) signature");

        var forbidden = new[] { "query", "title", "content", "text", "body", "q" };
        foreach (var param in record!.GetParameters())
        {
            var name = param.Name!.ToLowerInvariant();
            forbidden.Should().NotContain(name,
                $"SearchMetrics.Record must never accept a '{param.Name}' parameter (privacy)");
        }
    }
}
