namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Every threshold, score and size of the Dream feature in one place (plan 3.5, 3.6, 3.7). The similarity values are
/// starting points to calibrate on the planted corpus and on the test stand: e5 similarities are squeezed into the top
/// of the scale, so absolute numbers cannot be guessed. A test may run with changed values (<c>with</c>); production
/// code reads <see cref="Default"/> through DreamRunContext.Thresholds.
/// </summary>
public sealed record DreamThresholds
{
    public static DreamThresholds Default { get; } = new();

    // ── Similarity (3.5) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A strong neighbour is among this many nearest ...</summary>
    public int StrongNeighbourTopK { get; init; } = 20;
    /// <summary>... with Z at least this.</summary>
    public double StrongNeighbourMinZ { get; init; } = 2.0;
    /// <summary>A duplicate candidate has similarity at least this ...</summary>
    public double DuplicateCandidateMinSimilarity { get; init; } = 0.95;
    /// <summary>... or Z at least this.</summary>
    public double DuplicateCandidateMinZ { get; init; } = 4.0;
    /// <summary>Pair tier T2: the nearest this many with Z at least <see cref="Tier2MinZ"/>.</summary>
    public int Tier2TopK { get; init; } = 3;
    public double Tier2MinZ { get; init; } = 3.0;
    /// <summary>Pair tier T3: the nearest this many with Z at least <see cref="Tier3MinZ"/>.</summary>
    public int Tier3TopK { get; init; } = 5;
    public double Tier3MinZ { get; init; } = 2.0;
    /// <summary>T1 duplicate candidates, T2, T3. A scheduled run goes through T1 and T2 only.</summary>
    public int PairTierCount { get; init; } = 3;
    public int ScheduledMaxTier { get; init; } = 2;
    /// <summary>Phases on similarity are skipped when more than this share of articles has no vector after the
    /// embeddings phase.</summary>
    public double MaxShareWithoutVector { get; init; } = 0.20;

    // ── Detectors without a model (3.6) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Tag variants: same name after lower-casing and removing these characters (and white space).</summary>
    public string TagNormalizeRemovedChars { get; init; } = " -_.";
    public int TagVariantsScore { get; init; } = 85;

    public int UnusedTagsScore { get; init; } = 60;
    /// <summary>One unused-tags advice per run names at most this many tags.</summary>
    public int UnusedTagsMaxNames { get; init; } = 50;

    public int EmptyFolderScore { get; init; } = 55;
    /// <summary>An empty folder whose name looks like a test folder.</summary>
    public int EmptyTestFolderScore { get; init; } = 70;

    /// <summary>Test-leftover folder names, compared case-insensitively after stripping leading underscores; first or
    /// second tree level only. The last two are Russian "test" and "tests", spelled in escapes so the source stays
    /// ASCII.</summary>
    public IReadOnlyList<string> TestFolderNames { get; init; } =
        ["test", "tests", "tmp", "temp", "\u0442\u0435\u0441\u0442", "\u0442\u0435\u0441\u0442\u044B"];
    public int TestLeftoversMaxDepth { get; init; } = 2;
    public int TestLeftoversMaxArticles { get; init; } = 30;
    public int TestLeftoversScore { get; init; } = 50;

    /// <summary>Exact duplicate: same normalized title, or similarity at least this; and the same normalized-text hash.</summary>
    public double ExactDuplicateMinSimilarity { get; init; } = 0.98;
    public int ExactDuplicateScore { get; init; } = 95;

    /// <summary>Near pair (rules only, without the model): clamp(round(100 * sim) - offset, min, max).</summary>
    public int NearDuplicateScoreOffset { get; init; } = 25;
    public int NearDuplicateScoreMin { get; init; } = 50;
    public int NearDuplicateScoreMax { get; init; } = 75;

    /// <summary>Misplaced article: at least this many strong neighbours ...</summary>
    public int MisplacedMinStrongNeighbours { get; init; } = 8;
    /// <summary>... at least this share of them directly in one folder G ...</summary>
    public double MisplacedMinShare { get; init; } = 0.6;
    /// <summary>... at most this share in the article's own folder ...</summary>
    public double MisplacedMaxOwnShare { get; init; } = 0.15;
    /// <summary>... and the article unchanged for this many days. Score round(base + factor * share).</summary>
    public int MisplacedQuietDays { get; init; } = 3;
    public int MisplacedScoreBase { get; init; } = 40;
    public int MisplacedScoreFactor { get; init; } = 50;

    /// <summary>With the model on, detectors get at most this share of the run's advice limit.</summary>
    public double DetectorShareOfAdviceWithModel { get; init; } = 0.6;

    // ── Model step (3.7) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>pair_verdict sends at most head + tail characters of each article: the first and the last part.</summary>
    public int PairBodyHeadChars { get; init; } = 6000;
    public int PairBodyTailChars { get; init; } = 2000;
    public int PairMaxQuotes { get; init; } = 2;

    /// <summary>folder_review: folders with at least this many articles at their own level.</summary>
    public int FolderReviewMinArticles { get; init; } = 12;
    public int FolderReviewMaxArticles { get; init; } = 80;
    public int FolderReviewMaxNearbyFolders { get; init; } = 40;
    public int FolderReviewMaxTagsPerArticle { get; init; } = 5;
    public int FolderReviewSnippetChars { get; init; } = 200;
    public int FolderReviewMaxSuggestions { get; init; } = 8;

    /// <summary>tree_review: at most this many "path, article count" lines, this deep, at most once per this many days
    /// and only when the tree changed.</summary>
    public int TreeReviewMaxLines { get; init; } = 500;
    public int TreeReviewMaxDepth { get; init; } = 4;
    public int TreeReviewMaxSuggestions { get; init; } = 10;
    public int TreeReviewMinDays { get; init; } = 7;

    /// <summary>Model output fields are cut to these lengths.</summary>
    public int SummaryMaxLength { get; init; } = 400;
    public int QuoteMaxLength { get; init; } = 200;
    public int TitleMaxLength { get; init; } = 200;

    /// <summary>Waits when every key is exhausted, then provider_error. A 402/429 pauses a key this long.</summary>
    public IReadOnlyList<TimeSpan> ProviderBackoff { get; init; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];
    public TimeSpan KeyPause { get; init; } = TimeSpan.FromMinutes(5);

    // ── Housekeeping (3.4, 3.5) ─────────────────────────────────────────────────────────────────────────────────

    public TimeSpan SchedulerStep { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>An advice held in applying longer than this returns to new.</summary>
    public TimeSpan ApplyingTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>The detailed journal (dream_run_event) is kept this long.</summary>
    public TimeSpan EventRetention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>A queued, running or stopping run whose heartbeat is older than this has no live owner: interrupted.</summary>
    public TimeSpan HeartbeatStale { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A dismiss rule's default lifetime (or none).</summary>
    public int RuleDefaultExpiryDays { get; init; } = 180;

    // ── Score bands (3.4) ───────────────────────────────────────────────────────────────────────────────────────

    public const int HighBandMin = 80;
    public const int MediumBandMin = 60;
}

/// <summary>Score helpers shared by the engine, the detectors and the API.</summary>
public static class DreamScore
{
    public static int Clamp(int score) => Math.Clamp(score, 0, 100);

    public static DreamScoreBand BandOf(int score) =>
        score >= DreamThresholds.HighBandMin ? DreamScoreBand.High
        : score >= DreamThresholds.MediumBandMin ? DreamScoreBand.Medium
        : DreamScoreBand.Low;

    /// <summary>near_duplicate: clamp(round(100 * sim) - 25, 50, 75).</summary>
    public static int NearDuplicate(double similarity, DreamThresholds t) =>
        Math.Clamp((int)Math.Round(100 * similarity, MidpointRounding.AwayFromZero) - t.NearDuplicateScoreOffset,
            t.NearDuplicateScoreMin, t.NearDuplicateScoreMax);

    /// <summary>move_article: round(40 + 50 * share).</summary>
    public static int Misplaced(double share, DreamThresholds t) =>
        Clamp((int)Math.Round(t.MisplacedScoreBase + t.MisplacedScoreFactor * share, MidpointRounding.AwayFromZero));
}
