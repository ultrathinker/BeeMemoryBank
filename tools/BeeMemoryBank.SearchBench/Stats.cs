using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.SearchBench;

/// <summary>
/// Latency/throughput statistics over a single scenario run. One instance per scenario, serialized
/// to a JSON file under <c>_docs/search-100k/baseline/</c>.
/// </summary>
internal sealed class ScenarioResult
{
    public string Scenario { get; init; } = "";
    public string CorpusSizeLabel { get; init; } = "";
    public DateTime StartedAtUtc { get; init; }
    public DateTime EndedAtUtc { get; init; }
    public double DurationSeconds { get; init; }

    public long TotalRequests { get; init; }
    public long ErrorCount { get; init; }
    public long SuccessCount { get; init; }

    public double LatencyP50Ms { get; init; }
    public double LatencyP95Ms { get; init; }
    public double LatencyP99Ms { get; init; }
    public double LatencyMinMs { get; init; }
    public double LatencyMaxMs { get; init; }
    public double LatencyMeanMs { get; init; }

    public double ThroughputReqPerSec { get; init; }

    /// <summary>Concurrency used (1 for the closed-loop single-client scenarios, N for mixed).</summary>
    public int Concurrency { get; init; }

    /// <summary>Per-query breakdown when the scenario ran a fixed query mix (null for mixed load).</summary>
    public List<QueryBreakdown>? PerQuery { get; init; }

    /// <summary>Free-form notes (e.g. "embeddings not ready — skipped", "skipped: not requested").</summary>
    public string? Note { get; init; }

    /// <summary>Embedded-system allocations captured from the Api process, if available. Null when not measured.</summary>
    public long? ApiTotalAllocatedBytes { get; init; }
}

internal sealed class QueryBreakdown
{
    public string Query { get; init; } = "";
    public string Expectation { get; init; } = ""; // "frequent" | "rare"
    public int Samples { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MeanMs { get; init; }
    public double MinMs { get; init; }
    public double MaxMs { get; init; }
    public long ResultCount { get; init; } // from the last successful response (approximate indicator of selectivity)
}

/// <summary>Static helpers for latency percentile computation and JSON persistence.</summary>
internal static class Stats
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static double Percentile(double[] sortedAsc, double p)
    {
        if (sortedAsc.Length == 0) return 0;
        if (sortedAsc.Length == 1) return sortedAsc[0];
        // Linear interpolation between closest ranks (R-7, the common default).
        double rank = (p / 100.0) * (sortedAsc.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedAsc[lo];
        double frac = rank - lo;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * frac;
    }

    public static double Mean(double[] values)
    {
        if (values.Length == 0) return 0;
        double sum = 0;
        foreach (var v in values) sum += v;
        return sum / values.Length;
    }

    public static (double p50, double p95, double p99, double mean, double min, double max) Summary(double[] latenciesMs)
    {
        if (latenciesMs.Length == 0)
            return (0, 0, 0, 0, 0, 0);
        var sorted = (double[])latenciesMs.Clone();
        Array.Sort(sorted);
        return (
            Percentile(sorted, 50),
            Percentile(sorted, 95),
            Percentile(sorted, 99),
            Mean(sorted),
            sorted[0],
            sorted[^1]
        );
    }

    public static async Task WriteJsonAsync(string path, ScenarioResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = JsonSerializer.Serialize(result, JsonOpts);
        await File.WriteAllTextAsync(path, text);
    }
}
