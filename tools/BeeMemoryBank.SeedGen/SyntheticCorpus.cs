namespace BeeMemoryBank.SeedGen;

/// <summary>
/// A fully-generated article BEFORE it touches any service layer: pure plaintext body (never the
/// wrapped BMBENC1 blob — wrapping happens in <see cref="SeedRunner"/> via the codec) plus its
/// metadata. Pure and deterministic so tests can verify reproducibility without a database.
/// </summary>
public sealed record ArticleSpec(
    string Title,
    string TreePath,
    IReadOnlyList<string> Tags,
    string Body,
    string Locale,
    bool Protected);

/// <summary>
/// Produces the entire synthetic corpus (folders + article stream) deterministically from a single
/// integer seed. Folder generation uses its own RNG (seed-derived) so the article stream's RNG is
/// independent of how many folders were built. The article stream is lazy so a 100k-article run
/// never holds all bodies in memory at once.
/// </summary>
internal sealed class SyntheticCorpus
{
    // Fixed passphrase for the ~1% protected articles so later search WPs can unwrap them if needed.
    public const string ProtectedPassphrase = "seedgen-protected-1234";

    // Length buckets: ~85% normal (500 B – 4 KB), ~15% long (10 KB – 50 KB) to stress long-body paths.
    private const double LongArticleFraction = 0.15;
    private const double ProtectedFraction = 0.01;

    private readonly int _seed;
    private readonly WordPool _wordPool;
    private readonly IReadOnlyList<string> _topicWords;
    private readonly IReadOnlyList<string> _locales;
    private readonly List<string> _folders;
    private readonly ZipfSampler<string> _folderSampler;
    private readonly ZipfSampler<string> _tagSampler;

    public SyntheticCorpus(int seed, int articleCount, int folderCount, IReadOnlyList<string> locales)
    {
        _seed = seed;
        ArticleCount = articleCount;
        _locales = locales;
        _wordPool = WordPool.Load();
        _topicWords = TreeBuilder.CollectSegments(locales);

        // Folder tree uses a separately-seeded RNG so the article stream is identical regardless
        // of --folders (and vice-versa) — only --seed and the two counts drive everything.
        _folders = TreeBuilder.Build(new Random(seed ^ 0x5EED_F01D), folderCount, locales);
        _folderSampler = new ZipfSampler<string>(_folders);
        _tagSampler = new ZipfSampler<string>(TopicWords.Tags);
    }

    public int ArticleCount { get; }
    public IReadOnlyList<string> Folders => _folders;

    /// <summary>Lazily yields articles. Deterministic: same seed/count/locales → identical sequence.</summary>
    public IEnumerable<ArticleSpec> BuildArticles()
    {
        // Distinct seed from the folder RNG so the two streams evolve independently.
        var rng = new Random(_seed ^ 0xA17_C1E7);

        var localeText = new Dictionary<string, TextGenerator>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in _locales)
            localeText[loc] = TextGenerator.ForLocale(_wordPool, loc);

        for (int i = 0; i < ArticleCount; i++)
        {
            var locale = _locales[rng.Next(_locales.Count)];
            var treePath = _folderSampler.Sample(rng);
            var title = TitleGenerator.Generate(rng, _topicWords, _wordPool.ForLocale(locale));

            bool isProtected = rng.NextDouble() < ProtectedFraction;

            int tagCount = PickTagCount(rng);
            var tags = _tagSampler.SampleDistinct(rng, tagCount);

            int targetBytes = PickTargetBytes(rng);
            var body = localeText[locale].Generate(rng, targetBytes);

            yield return new ArticleSpec(title, treePath, tags, body, locale, isProtected);
        }
    }

    private static int PickTagCount(Random rng)
    {
        // 0..6, weighted toward the lower end (most articles have 1–3 tags).
        double r = rng.NextDouble();
        if (r < 0.10) return 0;
        if (r < 0.30) return 1;
        if (r < 0.55) return 2;
        if (r < 0.75) return 3;
        if (r < 0.88) return 4;
        if (r < 0.96) return 5;
        return 6;
    }

    private static int PickTargetBytes(Random rng)
    {
        if (rng.NextDouble() < LongArticleFraction)
        {
            // 10 KB – 50 KB, Zipf-ish toward the smaller end of the long bucket.
            double r = rng.NextDouble();
            int kb = (int)(10 + Math.Round(40 * r * r)); // 10..50
            return kb * 1024;
        }
        // 500 B – 4 KB.
        return 500 + rng.Next(3597); // 500..4096
    }
}
