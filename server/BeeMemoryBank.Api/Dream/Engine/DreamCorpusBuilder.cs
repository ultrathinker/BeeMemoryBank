using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>How many in-scope articles there are, how many wait for a vector, and how many have none.</summary>
public sealed record DreamEmbeddingCoverage(int Total, int Pending, int WithoutVector);

/// <summary>
/// Builds a run's IDreamCorpus (plan 3.5): one pass over the metadata without the access-scope filter (Dream runs for
/// the whole node, as the other background services do); not Protected, not a remote subscription, not in an excluded
/// folder. Bodies are read one at a time, and <see cref="DreamCorpus.ReadBodyAsync"/> checks the flag and
/// ProtectedContentCodec.IsProtected again immediately before it hands a text out.
/// </summary>
public sealed class DreamCorpusBuilder(IDbConnectionFactory mainDb, ArticleService articleService, SearchService search)
{
    public async Task<DreamCorpus> BuildAsync(DreamSettings settings, CancellationToken ct)
    {
        using var conn = mainDb.CreateConnection();

        var folderRows = (await conn.QueryAsync<FolderRow>(
            """
            SELECT id AS Id, path AS Path, is_system AS IsSystem, remote_subscription_id AS RemoteSubscriptionId
            FROM tbl_folder WHERE status = 'A'
            """)).ToList();
        ct.ThrowIfCancellationRequested();

        // The one definition of what Dream hides, shared with Apply's move checks (DreamAdviceValidator).
        var hiddenRoots = await HiddenRootsAsync(conn, settings.ExcludedFolderIds);

        var articleRows = (await conn.QueryAsync<ArticleRow>(
            """
            SELECT a.id AS Id, a.title AS Title, COALESCE(f.path, '/') AS TreePath, a.folder_id AS FolderId, a.lamport_ts AS LamportTs,
                   a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.embedding_projection AS Projection,
                   a.protected AS IsProtected, a.remote_subscription_id AS RemoteSubscriptionId
            FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
            WHERE a.status = 'A'
            """)).ToList();
        ct.ThrowIfCancellationRequested();

        // Counted over every active article, Protected, remote and excluded ones too: a folder holding only those is not
        // empty, and "empty" is what gets a deletion proposed.
        var direct = new Dictionary<string, int>(StringComparer.Ordinal);
        var subtree = new Dictionary<string, int>(StringComparer.Ordinal);
        var inScope = new List<DreamArticle>();
        var vectors = new Dictionary<Guid, float[]>();
        foreach (var row in articleRows)
        {
            direct[row.TreePath] = direct.GetValueOrDefault(row.TreePath) + 1;
            foreach (var ancestor in SelfAndAncestors(row.TreePath))
                subtree[ancestor] = subtree.GetValueOrDefault(ancestor) + 1;

            if (row.IsProtected != 0 || row.RemoteSubscriptionId != null || IsUnderAny(row.TreePath, hiddenRoots)) continue;

            var id = Guid.Parse(row.Id);
            var hasVector = row.Projection is { Length: > 0 };
            inScope.Add(new DreamArticle(id, row.Title, row.TreePath, row.FolderId is null ? null : Guid.Parse(row.FolderId),
                row.LamportTs, ParseTime(row.CreatedAt), ParseTime(row.UpdatedAt), hasVector));
            if (hasVector) vectors[id] = MemoryMarshal.Cast<byte, float>(row.Projection).ToArray();
        }

        var folders = folderRows
            .Where(f => f.RemoteSubscriptionId == null && !IsUnderAny(f.Path, hiddenRoots))
            .Select(f => new DreamFolder(Guid.Parse(f.Id), f.Path, direct.GetValueOrDefault(f.Path), subtree.GetValueOrDefault(f.Path), f.IsSystem != 0))
            .ToList();

        var inScopeIds = inScope.Select(a => a.Id).ToHashSet();
        var tagRows = await conn.QueryAsync<TagRow>(
            """
            SELECT ct.name AS Name, a.id AS ArticleId
            FROM tbl_concept_tag ct
            LEFT JOIN tbl_article_concept_tag act ON act.concept_tag_id = ct.id
            LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
            """);
        var tags = new List<DreamTag>();
        var tagsOf = new Dictionary<Guid, List<string>>();
        foreach (var group in tagRows.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            var active = group.Where(t => t.ArticleId != null).Select(t => Guid.Parse(t.ArticleId!)).Distinct().ToList();
            var visible = active.Where(inScopeIds.Contains).ToList();

            // A tag only hidden articles carry is neither unused (its articles are alive) nor to be named (its articles
            // are fenced off), so it stays out of the list altogether.
            if (active.Count > 0 && visible.Count == 0) continue;

            tags.Add(new DreamTag(group.Key, active.Count));
            foreach (var id in visible)
            {
                if (!tagsOf.TryGetValue(id, out var list)) tagsOf[id] = list = [];
                list.Add(group.Key);
            }
        }

        return new DreamCorpus(inScope, folders, tags, tagsOf, vectors, hiddenRoots, settings.ExcludedFolderIds, mainDb, articleService, search);
    }

    /// <summary>
    /// The coverage of the articles Dream may look at (GET /status, the embeddings phase, the night check): the same scope
    /// as the corpus, so missing vectors in an excluded or remote subtree neither skip a night nor show in the counts.
    /// </summary>
    public async Task<DreamEmbeddingCoverage> GetEmbeddingCoverageAsync(DreamSettings settings)
    {
        using var conn = mainDb.CreateConnection();
        var roots = await HiddenRootsAsync(conn, settings.ExcludedFolderIds);
        var args = new DynamicParameters();
        var outside = new List<string>();
        for (var i = 0; i < roots.Count; i++)
        {
            args.Add($"r{i}", roots[i]);
            outside.Add($"(p = @r{i} OR substr(p, 1, length(@r{i}) + 1) = @r{i} || '/')");
        }
        var filter = outside.Count == 0 ? "" : $"WHERE NOT ({string.Join(" OR ", outside)})";
        var row = await conn.QuerySingleAsync<(long Total, long Pending, long WithoutVector)>(
            $"""
            SELECT COUNT(*), COALESCE(SUM(pending), 0), COALESCE(SUM(novector), 0)
            FROM (SELECT COALESCE(f.path, '/') AS p, a.embedding_pending != 0 AS pending,
                         (a.embedding_projection IS NULL OR length(a.embedding_projection) = 0) AS novector
                  FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
                  WHERE a.status = 'A' AND a.protected = 0 AND a.remote_subscription_id IS NULL)
            {filter}
            """, args);
        return new DreamEmbeddingCoverage((int)row.Total, (int)row.Pending, (int)row.WithoutVector);
    }

    /// <summary>
    /// The roots Dream hides now, as the folders are now: the current path of every folder whose id the settings exclude
    /// (a renamed folder stays excluded under its new path) and of every remote subscription's folder. Nothing at or below
    /// one (DreamCorpusBuilder.IsUnderAny, on segment boundaries) is read, named, or the target or source of an Apply.
    /// </summary>
    public async Task<IReadOnlyList<string>> HiddenRootsAsync(DreamSettings settings)
    {
        using var conn = mainDb.CreateConnection();
        return await HiddenRootsAsync(conn, settings.ExcludedFolderIds);
    }

    /// <summary>The current paths of the excluded folders and of every remote subscription's folders: the subtrees Dream neither reads nor names.</summary>
    internal static async Task<List<string>> HiddenRootsAsync(IDbConnection conn, IReadOnlyCollection<Guid> excludedFolderIds)
    {
        var rows = await conn.QueryAsync<string>(
            """
            SELECT path FROM tbl_folder
            WHERE status = 'A' AND (remote_subscription_id IS NOT NULL OR id IN @excluded)
            """,
            new { excluded = Spellings(excludedFolderIds) });
        return rows.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A Guid is stored in the main database in the case its writer bound it in: both spellings, for an IN list.</summary>
    internal static string[] Spellings(IEnumerable<Guid> ids) =>
        ids.SelectMany(id => new[] { id.ToString("D"), id.ToString("D").ToUpperInvariant() }).ToArray();

    /// <summary>Which of the ids still exist in the main database. A trashed article exists; a hard-deleted one does not.</summary>
    public async Task<IReadOnlySet<Guid>> FindExistingArticlesAsync(IReadOnlyCollection<Guid> ids)
    {
        // A Guid is written to the main database in the case its writer bound it in, so both spellings are asked for; the
        // sweep deletes on a miss, and a wrong miss would delete good advice.
        var found = new HashSet<Guid>();
        using var conn = mainDb.CreateConnection();
        foreach (var batch in ids.Chunk(250))
        {
            var spellings = Spellings(batch);
            foreach (var id in await conn.QueryAsync<string>("SELECT id FROM tbl_article WHERE id IN @spellings", new { spellings }))
                found.Add(Guid.Parse(id));
        }
        return found;
    }

    internal static IEnumerable<string> SelfAndAncestors(string path)
    {
        var current = path;
        while (current.Length > 1)
        {
            yield return current;
            var cut = current.LastIndexOf('/');
            if (cut <= 0) break;
            current = current[..cut];
        }
    }

    internal static bool IsUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);

    internal static bool IsUnderAny(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
            if (IsUnder(path, root)) return true;
        return false;
    }

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed class FolderRow
    {
        public string Id { get; set; } = "";
        public string Path { get; set; } = "";
        public long IsSystem { get; set; }
        public string? RemoteSubscriptionId { get; set; }
    }

    private sealed class ArticleRow
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string TreePath { get; set; } = "/";
        public string? FolderId { get; set; }
        public long LamportTs { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public byte[]? Projection { get; set; }
        public long IsProtected { get; set; }
        public string? RemoteSubscriptionId { get; set; }
    }

    private sealed class TagRow
    {
        public string Name { get; set; } = "";
        public string? ArticleId { get; set; }
    }
}

/// <summary>
/// What one run may look at. Lives as long as the run's DI scope.
///
/// <para>The lists are taken when the run starts, but an article can be protected, moved under an excluded folder or
/// deleted while the run works. Every body read and every write that names an article asks the main database again
/// (<see cref="RevalidateAsync"/>); an article found out of reach is withdrawn: from then on it is not listed, has no
/// neighbours or tags, is never read, and a proposal naming it is not accepted.</para>
/// </summary>
public sealed class DreamCorpus : IDreamCorpus
{
    private readonly IReadOnlyList<DreamArticle> _articles;
    private readonly Dictionary<Guid, DreamArticle> _byId;
    private readonly HashSet<Guid> _folderIds;
    private readonly HashSet<string> _tagNames;
    private readonly IReadOnlyDictionary<Guid, List<string>> _tagsOf;
    private readonly IReadOnlyList<string> _hiddenRoots;
    private readonly IReadOnlyCollection<Guid> _excludedFolderIds;
    private readonly Lazy<DreamNeighbourIndex> _neighbours;
    private readonly IDbConnectionFactory _mainDb;
    private readonly ArticleService _articleService;
    private readonly SearchService _search;
    private readonly ConcurrentDictionary<Guid, byte> _withdrawn = new();
    private readonly object _listGate = new();
    private IReadOnlyList<DreamArticle>? _visible;

    internal DreamCorpus(IReadOnlyList<DreamArticle> articles, IReadOnlyList<DreamFolder> folders, IReadOnlyList<DreamTag> tags,
        IReadOnlyDictionary<Guid, List<string>> tagsOf, IReadOnlyDictionary<Guid, float[]> vectors, IReadOnlyList<string> hiddenRoots,
        IReadOnlyCollection<Guid> excludedFolderIds, IDbConnectionFactory mainDb, ArticleService articleService, SearchService search)
    {
        _articles = articles;
        Folders = folders;
        Tags = tags;
        _byId = articles.ToDictionary(a => a.Id);
        _folderIds = folders.Select(f => f.Id).ToHashSet();
        _tagNames = tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        _tagsOf = tagsOf;
        _hiddenRoots = hiddenRoots;
        _excludedFolderIds = excludedFolderIds;
        _neighbours = new Lazy<DreamNeighbourIndex>(() => new DreamNeighbourIndex(vectors));
        _mainDb = mainDb;
        _articleService = articleService;
        _search = search;
    }

    /// <summary>The articles of the run, without those withdrawn since it started.</summary>
    public IReadOnlyList<DreamArticle> Articles
    {
        get
        {
            lock (_listGate)
                return _visible ??= _withdrawn.IsEmpty ? _articles : _articles.Where(a => !_withdrawn.ContainsKey(a.Id)).ToList();
        }
    }

    public IReadOnlyList<DreamFolder> Folders { get; }
    public IReadOnlyList<DreamTag> Tags { get; }

    /// <summary>False once too many articles lack a vector for similarity to mean anything (plan 3.5).</summary>
    public bool SimilarityEnabled { get; private set; } = true;

    internal void DisableSimilarity() => SimilarityEnabled = false;

    public bool HasArticle(Guid id) => _byId.ContainsKey(id) && !_withdrawn.ContainsKey(id);

    /// <summary>True for an article of this run that has since been found out of reach.</summary>
    public bool IsWithdrawn(Guid id) => _withdrawn.ContainsKey(id);

    public bool HasFolder(Guid id) => _folderIds.Contains(id);

    public bool HasTag(string name) => _tagNames.Contains(name);

    /// <summary>True for a path inside an excluded folder's subtree or a remote subscription (as the run started): Dream does not name it.</summary>
    public bool IsHiddenPath(string path) => DreamCorpusBuilder.IsUnderAny(path, _hiddenRoots);

    public IReadOnlyList<string> TagsOf(Guid articleId) =>
        HasArticle(articleId) && _tagsOf.TryGetValue(articleId, out var tags) ? tags : [];

    public IReadOnlyList<DreamNeighbour> NeighboursOf(Guid articleId, int topK)
    {
        if (!SimilarityEnabled || !HasArticle(articleId)) return [];
        var near = _neighbours.Value.NeighboursOf(articleId, topK);
        return _withdrawn.IsEmpty ? near : near.Where(n => !_withdrawn.ContainsKey(n.Id)).ToList();
    }

    public async Task<string?> ReadBodyAsync(Guid articleId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The corpus is minutes old by now: the article may have been protected, moved under an excluded folder or deleted since.
        if ((await RevalidateAsync([articleId], ct)).Count > 0) return null;

        string content;
        try
        {
            content = await _articleService.GetContentAsync(articleId);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or CryptographicException)
        {
            return null;
        }

        // And again after the read: a change that landed while the body was being decrypted refuses the text just read.
        if ((await RevalidateAsync([articleId], ct)).Count > 0) return null;
        return ProtectedContentCodec.IsProtected(content) ? null : content;
    }

    /// <summary>
    /// Asks the main database which of <paramref name="ids"/> are out of reach now: not an article of this run, deleted,
    /// Protected, mirrored from a remote node, or under an excluded or remote folder as the folders are now. Those are
    /// withdrawn and returned; an empty answer means every one may still be read and named.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> RevalidateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var refused = new HashSet<Guid>();
        var ask = new List<Guid>();
        foreach (var id in ids.Distinct())
            if (HasArticle(id)) ask.Add(id);
            else refused.Add(id);
        if (ask.Count == 0) return refused;

        using var conn = _mainDb.CreateConnection();
        var roots = await DreamCorpusBuilder.HiddenRootsAsync(conn, _excludedFolderIds);
        var current = (await conn.QueryAsync<StateRow>(
            """
            SELECT a.id AS Id, a.status AS Status, a.protected AS IsProtected, a.remote_subscription_id AS RemoteSubscriptionId,
                   COALESCE(f.path, '/') AS TreePath
            FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
            WHERE a.id IN @spellings
            """,
            new { spellings = DreamCorpusBuilder.Spellings(ask) })).ToDictionary(r => Guid.Parse(r.Id));

        foreach (var id in ask)
        {
            if (current.TryGetValue(id, out var row) && row.Status == "A" && row.IsProtected == 0 && row.RemoteSubscriptionId is null
                && !DreamCorpusBuilder.IsUnderAny(row.TreePath, roots))
                continue;
            refused.Add(id);
            Withdraw(id);
        }
        return refused;
    }

    private void Withdraw(Guid id)
    {
        if (!_withdrawn.TryAdd(id, 0)) return;
        lock (_listGate) _visible = null;
    }

    private sealed class StateRow
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public long IsProtected { get; set; }
        public string? RemoteSubscriptionId { get; set; }
        public string TreePath { get; set; } = "/";
    }

    /// <summary>The same search Apply repeats before it trashes a copy (DreamReferenceChecker).</summary>
    public async Task<int> CountReferencesAsync(Guid articleId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await DreamReferenceChecker.CountAsync(_search, articleId);
    }
}
