using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.SeedGen;

/// <summary>
/// Inverse-CDF sampler over a Zipf (power-law) distribution over a fixed set of items.
/// Rank-0 is the most popular item; P(rank) ∝ 1 / (rank+1)^exponent. Sampling is O(log N)
/// per draw via a precomputed cumulative-weight table. Deterministic given the supplied
/// <see cref="Random"/> sequence — no internal entropy.
/// </summary>
internal sealed class ZipfSampler<T>
{
    private readonly T[] _items;
    private readonly double[] _cumulative;
    private readonly double _total;

    public int Count => _items.Length;

    public ZipfSampler(IReadOnlyList<T> items, double exponent = 1.0)
    {
        if (items.Count == 0) throw new ArgumentException("Zipf pool must not be empty.", nameof(items));
        _items = items as T[] ?? [.. items];
        _cumulative = new double[_items.Length];
        double sum = 0;
        for (int i = 0; i < _items.Length; i++)
        {
            sum += 1.0 / Math.Pow(i + 1, exponent);
            _cumulative[i] = sum;
        }
        _total = sum;
    }

    public T Sample(Random rng)
    {
        double r = rng.NextDouble() * _total;
        int lo = 0, hi = _cumulative.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cumulative[mid] > r) hi = mid;
            else lo = mid + 1;
        }
        return _items[lo];
    }

    /// <summary>Distinct samples (reservoir-free rejection for small counts).</summary>
    public List<T> SampleDistinct(Random rng, int count)
    {
        if (count > _items.Length) count = _items.Length;
        var picked = new List<T>(count);
        if (count == 0) return picked;
        var seen = new HashSet<T>();
        while (picked.Count < count)
        {
            var item = Sample(rng);
            if (seen.Add(item)) picked.Add(item);
        }
        return picked;
    }
}

/// <summary>
/// Loads the English word pool from the embedded BERT vocab and pairs it with the curated
/// Russian pool. Provides locale-resolved Zipf samplers.
/// </summary>
internal sealed class WordPool
{
    private static readonly Regex CleanEnglishWord = new("^[a-z][a-z]+$", RegexOptions.Compiled);

    private readonly ZipfSampler<string> _en;
    private readonly ZipfSampler<string> _ru;

    private WordPool(IReadOnlyList<string> englishWords, IReadOnlyList<string> russianWords)
    {
        _en = new ZipfSampler<string>(englishWords);
        _ru = new ZipfSampler<string>(russianWords);
    }

    public ZipfSampler<string> ForLocale(string locale) =>
        locale.Equals("ru", StringComparison.OrdinalIgnoreCase) ? _ru : _en;

    /// <summary>
    /// Reads the embedded vocab.txt, strips BERT bookkeeping (special [..] tokens, ## WordPiece
    /// continuation pieces, single letters, punctuation/digits), and returns the surviving
    /// lowercase-letters-only word tokens. Order is preserved so the Zipf ranks are stable.
    /// </summary>
    public static WordPool Load()
    {
        var english = LoadEmbeddedVocab();
        return new WordPool(english, RussianWords.Words);
    }

    private static IReadOnlyList<string> LoadEmbeddedVocab()
    {
        // Anchor to the SeedGen assembly (where the resource is embedded), not GetExecutingAssembly,
        // so callers from other assemblies (tests) still find it.
        var assembly = typeof(WordPool).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("vocab.txt", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded vocab.txt resource not found.");

        var words = new List<string>(capacity: 24_000);
        using var stream = assembly.GetManifestResourceStream(name);
        using var reader = new StreamReader(stream ?? throw new InvalidOperationException("vocab stream null"), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var token = line.Trim();
            if (token.Length < 2) continue;
            if (token[0] == '[') continue;          // [PAD], [CLS], [SEP], [unused*], ...
            if (token.StartsWith("##", StringComparison.Ordinal)) continue; // WordPiece continuation
            if (!CleanEnglishWord.IsMatch(token)) continue; // keep pure multi-letter lowercase words
            words.Add(token);
        }

        if (words.Count == 0)
            throw new InvalidOperationException("vocab.txt yielded no usable English word tokens.");
        return words;
    }
}
