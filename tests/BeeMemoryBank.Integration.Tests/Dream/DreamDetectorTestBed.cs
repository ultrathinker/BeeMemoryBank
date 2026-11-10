using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// A hand-written <see cref="IDreamCorpus"/> for the rule and similarity detector tests: no node, no database and
/// no embeddings model. Every neighbour row is given explicitly, so the tests state the similarity matrix they
/// mean instead of deriving one from vectors.
/// </summary>
internal sealed class HandMadeDreamCorpus : IDreamCorpus
{
    private static readonly DateTime DefaultTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly List<DreamArticle> _articles = [];
    private readonly List<DreamFolder> _folders = [];
    private readonly List<DreamTag> _tags = [];
    private readonly Dictionary<Guid, List<string>> _tagsOf = [];
    private readonly Dictionary<Guid, List<DreamNeighbour>> _neighbours = [];
    private readonly Dictionary<Guid, string> _bodies = [];
    private readonly Dictionary<Guid, int> _references = [];
    private readonly Dictionary<Guid, int> _bodyReads = [];

    public IReadOnlyList<DreamArticle> Articles => _articles;
    public IReadOnlyList<DreamFolder> Folders => _folders;
    public IReadOnlyList<DreamTag> Tags => _tags;

    /// <summary>How many times each article's body was read, so a test can prove the bodies are hashed once.</summary>
    public IReadOnlyDictionary<Guid, int> BodyReads => _bodyReads;

    public int BodyReadsOf(DreamArticle article) => _bodyReads.GetValueOrDefault(article.Id);

    public DreamArticle AddArticle(
        string title,
        string treePath,
        DateTime? createdAt = null,
        DateTime? updatedAt = null,
        long lamportTs = 1,
        bool hasEmbedding = true)
    {
        var article = new DreamArticle(
            Guid.NewGuid(), title, treePath, null, lamportTs,
            createdAt ?? DefaultTime, updatedAt ?? createdAt ?? DefaultTime, hasEmbedding);
        _articles.Add(article);
        return article;
    }

    public HandMadeDreamCorpus AddFolder(
        string path, bool isSystem = false, int directArticles = 0, int subtreeArticles = 0, Guid? id = null)
    {
        _folders.Add(new DreamFolder(id ?? Guid.NewGuid(), path, directArticles, subtreeArticles, isSystem));
        return this;
    }

    public HandMadeDreamCorpus AddTag(string name, int articleCount)
    {
        _tags.Add(new DreamTag(name, articleCount));
        return this;
    }

    public HandMadeDreamCorpus WithBody(DreamArticle article, string body)
    {
        _bodies[article.Id] = body;
        return this;
    }

    public HandMadeDreamCorpus WithReferences(DreamArticle article, int count)
    {
        _references[article.Id] = count;
        return this;
    }

    public HandMadeDreamCorpus WithTagsOf(DreamArticle article, params string[] tags)
    {
        _tagsOf[article.Id] = [.. tags];
        return this;
    }

    /// <summary>Both directions of one pair. The Z of each direction is given separately because the real Z is
    /// relative to the row it appears in, not to the pair.</summary>
    public HandMadeDreamCorpus Link(
        DreamArticle a, DreamArticle b, double similarity, double zFromA = 0, double? zFromB = null)
    {
        WithNeighbour(a.Id, b.Id, similarity, zFromA);
        WithNeighbour(b.Id, a.Id, similarity, zFromB ?? zFromA);
        return this;
    }

    /// <summary>One direction of a pair; also how a test names a neighbour that is not an article of the corpus.</summary>
    public HandMadeDreamCorpus WithNeighbour(Guid fromId, Guid toId, double similarity, double z)
    {
        if (!_neighbours.TryGetValue(fromId, out var list)) _neighbours[fromId] = list = [];
        list.Add(new DreamNeighbour(toId, similarity, z));
        return this;
    }

    public IReadOnlyList<string> TagsOf(Guid articleId) =>
        _tagsOf.TryGetValue(articleId, out var tags) ? tags : [];

    public IReadOnlyList<DreamNeighbour> NeighboursOf(Guid articleId, int topK)
    {
        if (topK <= 0 || !_neighbours.TryGetValue(articleId, out var neighbours)) return [];
        return neighbours
            .OrderByDescending(n => n.Similarity)
            .ThenBy(n => n.Id.ToString("D"), StringComparer.Ordinal)
            .Take(topK)
            .ToList();
    }

    public Task<string?> ReadBodyAsync(Guid articleId, CancellationToken ct)
    {
        _bodyReads[articleId] = _bodyReads.GetValueOrDefault(articleId) + 1;
        return Task.FromResult(_bodies.TryGetValue(articleId, out var body) ? body : null);
    }

    public Task<int> CountReferencesAsync(Guid articleId, CancellationToken ct) =>
        Task.FromResult(_references.TryGetValue(articleId, out var count) ? count : 0);
}

internal sealed class FixedDreamClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class RecordingDreamLog : IDreamRunLog
{
    public List<(DreamEventKind Kind, string Text)> Lines { get; } = [];

    public Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null,
        JsonObject? data = null, CancellationToken ct = default)
    {
        Lines.Add((kind, text));
        return Task.CompletedTask;
    }
}

internal static class DreamTestRuns
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public static DreamRunContext Context(
        bool useModel = false, DreamThresholds? thresholds = null, DateTimeOffset? now = null)
    {
        var clock = new FixedDreamClock(now ?? DefaultNow);
        return new DreamRunContext
        {
            RunId = Guid.NewGuid(),
            Trigger = DreamRunTrigger.Manual,
            Mode = DreamRunMode.Normal,
            Settings = DreamSettings.Default,
            Thresholds = thresholds ?? DreamThresholds.Default,
            UseModel = useModel,
            Budget = new DreamRunBudget(10, 1_000_000),
            Log = new RecordingDreamLog(),
            Clock = clock,
            StartedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
    }
}

internal static class DreamDetectorRunner
{
    public static async Task<List<DreamAdviceDraft>> CollectAsync(
        IDreamDetector detector, IDreamCorpus corpus, DreamRunContext ctx)
    {
        var drafts = new List<DreamAdviceDraft>();
        await foreach (var draft in detector.DetectAsync(corpus, ctx, CancellationToken.None)) drafts.Add(draft);
        return drafts;
    }
}
