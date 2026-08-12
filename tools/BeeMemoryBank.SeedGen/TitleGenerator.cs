namespace BeeMemoryBank.SeedGen;

/// <summary>
/// Deterministic one-line titles assembled from topic words, sampled prose words, and a few
/// date/quarter/number templates so the corpus looks like a real notes archive.
/// </summary>
internal static class TitleGenerator
{
    public static string Generate(Random rng, IReadOnlyList<string> topicWords, ZipfSampler<string> words)
    {
        var topic = topicWords[rng.Next(topicWords.Count)];
        int template = rng.Next(6);
        return template switch
        {
            0 => $"{Cap(topic)} {Cap(words.Sample(rng))}",
            1 => $"{Cap(topic)} — {Cap(words.Sample(rng))} {Cap(words.Sample(rng))}",
            2 => $"{Quarter(rng)} {Cap(topic)} Review",
            3 => $"{Cap(topic)} Notes ({Year(rng)}-{Month(rng):D2})",
            4 => $"{Cap(words.Sample(rng))} {Cap(topic)} #{1000 + rng.Next(9000)}",
            _ => $"{Cap(topic)} {Year(rng)}: {Cap(words.Sample(rng))} {Cap(words.Sample(rng))}"
        };
    }

    private static string Quarter(Random rng) => $"Q{1 + rng.Next(4)}";
    private static int Year(Random rng) => 2020 + rng.Next(7); // 2020..2026
    private static int Month(Random rng) => 1 + rng.Next(12);

    private static string Cap(string word) =>
        string.IsNullOrEmpty(word) ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
