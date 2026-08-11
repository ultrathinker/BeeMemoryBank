namespace BeeMemoryBank.SeedGen;

/// <summary>
/// Builds the folder tree as a deterministic list of paths (depth 3–5) with a Zipf-like shape:
/// a small set of "major" top-level categories (the first few topic words) are sampled with a
/// power law so a few big folders absorb most articles, while deeper segments are drawn from the
/// full topic pool producing a long tail of small folders.
/// </summary>
internal static class TreeBuilder
{
    public static List<string> Build(Random rng, int folderCount, IReadOnlyList<string> locales)
    {
        var segments = CollectSegments(locales);
        var segmentSampler = new ZipfSampler<string>(segments);

        int majorCount = Math.Clamp(folderCount / 15, 3, 10);
        var majors = segments.Take(majorCount).ToList();
        var majorSampler = new ZipfSampler<string>(majors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(folderCount);
        int guard = 0;
        while (result.Count < folderCount)
        {
            int depth = PickDepth(rng);
            var segs = new string[depth];
            segs[0] = majorSampler.Sample(rng);
            for (int i = 1; i < depth; i++)
                segs[i] = segmentSampler.Sample(rng);

            var path = "/" + string.Join('/', segs);
            if (seen.Add(path))
                result.Add(path);

            if (++guard > folderCount * 50 + 1000)
                break;
        }
        return result;
    }

    /// <summary>Depth in {3,4,5}, weighted toward shallower (most folders sit at depth 3).</summary>
    private static int PickDepth(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.55) return 3;
        if (r < 0.85) return 4;
        return 5;
    }

    /// <summary>Union of topic words for every active locale, in locale-list order.</summary>
    public static IReadOnlyList<string> CollectSegments(IReadOnlyList<string> locales)
    {
        var segments = new List<string>();
        foreach (var locale in locales)
        {
            if (locale.Equals("ru", StringComparison.OrdinalIgnoreCase))
                segments.AddRange(TopicWords.Ru);
            else
                segments.AddRange(TopicWords.En);
        }
        if (segments.Count == 0)
            segments.AddRange(TopicWords.En);
        return segments;
    }
}
