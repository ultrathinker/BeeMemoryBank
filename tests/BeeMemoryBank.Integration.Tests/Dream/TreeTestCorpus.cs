using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// A hand-made corpus with a folder tree, for the folder and tree review tests: folders (system ones too), notes with
/// bodies and tags, a note that cannot be read, and neighbours by vector given by hand. No node, no database, no model.
/// Folders are added with their ancestors, as the real corpus lists them, and their counts are worked out from the notes.
/// </summary>
public sealed class TreeTestCorpus : IDreamCorpus
{
    private readonly List<DreamArticle> _articles = [];
    private readonly Dictionary<Guid, string?> _bodies = [];
    private readonly Dictionary<Guid, string[]> _tags = [];
    private readonly Dictionary<string, (Guid Id, bool System)> _folders = new(StringComparer.Ordinal);

    /// <summary>Hand-made vector neighbours, best first.</summary>
    public Dictionary<Guid, List<DreamNeighbour>> Neighbours { get; } = [];

    /// <summary>Notes whose text may not be read (the corpus answers null).</summary>
    public HashSet<Guid> Unreadable { get; } = [];

    /// <summary>Roots the corpus keeps out of sight (an excluded subtree, a remote subscription): it lists none of them, and
    /// <see cref="IsHiddenPath"/> says yes to a path at or below one.</summary>
    public List<string> Hidden { get; } = [];

    public bool IsHiddenPath(string path) => Hidden.Any(h => path == h || path.StartsWith(h + "/", StringComparison.Ordinal));

    public IReadOnlyList<DreamArticle> Articles => _articles.ToList();

    public IReadOnlyList<DreamFolder> Folders => _folders.Select(f => new DreamFolder(f.Value.Id, f.Key,
        _articles.Count(a => a.TreePath == f.Key),
        _articles.Count(a => a.TreePath == f.Key || a.TreePath.StartsWith(f.Key + "/", StringComparison.Ordinal)),
        f.Value.System)).ToList();

    public IReadOnlyList<DreamTag> Tags => [];

    public IReadOnlyList<string> TagsOf(Guid articleId) => _tags.TryGetValue(articleId, out var t) ? t : [];

    public IReadOnlyList<DreamNeighbour> NeighboursOf(Guid articleId, int topK) =>
        Neighbours.TryGetValue(articleId, out var list) ? list.Take(topK).ToList() : [];

    public Task<string?> ReadBodyAsync(Guid articleId, CancellationToken ct) =>
        Task.FromResult(Unreadable.Contains(articleId) ? null : _bodies.GetValueOrDefault(articleId));

    public Task<int> CountReferencesAsync(Guid articleId, CancellationToken ct) => Task.FromResult(0);

    public Guid FolderId(string path) => _folders[path].Id;

    /// <summary>Adds a folder and its ancestors; the first call decides whether it is a system folder. Returns its id.</summary>
    public Guid AddFolder(string path, bool system = false)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        for (var i = 0; i < segments.Length; i++)
        {
            current += "/" + segments[i];
            if (!_folders.ContainsKey(current)) _folders[current] = (Guid.NewGuid(), system && i == segments.Length - 1);
        }
        return _folders[path].Id;
    }

    public DreamArticle AddNote(string path, string title, string? body = "Some text.", long lamport = 100, DateTime? updated = null,
        bool embedding = true, params string[] tags)
    {
        if (path != "/") AddFolder(path);
        var when = updated ?? new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var a = new DreamArticle(Guid.NewGuid(), title, path, path == "/" ? null : FolderId(path), lamport,
            when.AddDays(-30), when, embedding);
        _articles.Add(a);
        _bodies[a.Id] = body;
        _tags[a.Id] = tags;
        return a;
    }

    /// <summary>Adds <paramref name="count"/> plain notes, each changed a day after the one before.</summary>
    public List<DreamArticle> AddNotes(string path, int count, string titlePrefix = "Note")
    {
        var list = new List<DreamArticle>();
        for (var i = 1; i <= count; i++)
            list.Add(AddNote(path, $"{titlePrefix} {i}", $"Text of {titlePrefix.ToLowerInvariant()} {i}.", 100 + i,
                new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)));
        return list;
    }

    /// <summary>Replaces a note by a changed copy (an edit: a new LamportTs, say).</summary>
    public void Change(Guid id, Func<DreamArticle, DreamArticle> edit)
    {
        var i = _articles.FindIndex(a => a.Id == id);
        _articles[i] = edit(_articles[i]);
    }

    /// <summary>Gives a note strong neighbours (Z = 3) among <paramref name="others"/>.</summary>
    public void Link(Guid id, IEnumerable<DreamArticle> others, double z = 3.0) =>
        Neighbours[id] = others.Select((o, i) => new DreamNeighbour(o.Id, 0.9 - i * 0.001, z)).ToList();

    /// <summary>A syntactically valid protected blob (the codec's own structural check), made without the passphrase machinery.</summary>
    public static string ProtectedBlob()
    {
        var payload = new List<byte> { 0x01 };
        payload.AddRange(new byte[12]);
        payload.Add(16);
        payload.AddRange(new byte[16]);
        payload.Add(12);
        payload.AddRange(new byte[12]);
        payload.AddRange(new byte[40]);
        return "BMBENC1:" + Convert.ToBase64String(payload.ToArray());
    }
}

/// <summary>The journal of a test run: every line, in order.</summary>
public sealed class TreeTestLog : IDreamRunLog
{
    public List<(DreamEventKind Kind, string Text, JsonObject? Data)> Lines { get; } = [];

    public Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
        CancellationToken ct = default)
    {
        Lines.Add((kind, text, data));
        return Task.CompletedTask;
    }

    public string AllText => string.Join("\n", Lines.Select(l => l.Text + " " + l.Data));
}

/// <summary>A clock a test sets.</summary>
public sealed class TreeTestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>The run context of a folder or tree review test.</summary>
public static class TreeTestContext
{
    public static DreamRunContext Make(DreamRunBudget? budget = null, TreeTestLog? log = null, string? modelId = "test/model",
        DreamRunMode mode = DreamRunMode.Normal, DreamSampleRequest? sample = null, TimeProvider? clock = null) => new()
    {
        RunId = Guid.NewGuid(),
        Trigger = DreamRunTrigger.Manual,
        Mode = mode,
        Settings = DreamSettings.Default,
        UseModel = true,
        ModelId = modelId,
        ModelLabel = "Test model",
        Budget = budget ?? new DreamRunBudget(200, 1_500_000),
        Log = log ?? new TreeTestLog(),
        Sample = sample,
        Clock = clock ?? TimeProvider.System,
        StartedAtUtc = DateTime.UtcNow,
    };

    public static async Task<List<T>> ToList<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
