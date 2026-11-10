using System.Text.RegularExpressions;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>A folder of the corpus under its canonical path.</summary>
internal sealed record IndexedFolder(DreamFolder Source, string Path)
{
    public Guid Id => Source.Id;
}

/// <summary>
/// What folder_review and tree_review need to know about the tree of one corpus: the articles by id, the folders by path (system folders and everything below them left out, so they are
/// never reviewed, named to the model or offered as a destination), and the articles of each such folder.
/// </summary>
internal sealed class DreamTreeIndex
{
    public required IReadOnlyDictionary<Guid, DreamArticle> ById { get; init; }

    /// <summary>The usable folders by canonical path (ordinal: paths are case sensitive).</summary>
    public required IReadOnlyDictionary<string, IndexedFolder> FoldersByPath { get; init; }

    public required IReadOnlyDictionary<Guid, IndexedFolder> FoldersById { get; init; }

    /// <summary>The corpus articles of each usable folder, by the article's own folder path; "/" holds the loose ones.</summary>
    public required IReadOnlyDictionary<string, List<DreamArticle>> MembersByPath { get; init; }

    public required IReadOnlyList<string> SystemPaths { get; init; }

    /// <summary>The canonical folder path of each article outside the system folders.</summary>
    public required IReadOnlyDictionary<Guid, string> PathById { get; init; }

    /// <summary>What the corpus keeps out of sight (excluded subtrees, remote subscriptions): <see cref="IDreamCorpus.IsHiddenPath"/>.</summary>
    public required Func<string, bool> Hidden { get; init; }

    /// <summary>
    /// True for a path Dream must not propose as a destination or a name: a system folder or anything below one, or a
    /// path the corpus hides. These paths are kept here only to be refused; they are never put in an input to the model.
    /// A hidden folder is left out of the listing, so the listing cannot say that a proposed new path already exists
    /// there: this is the check that does.
    /// </summary>
    public bool IsBlocked(string path)
    {
        var canonical = TreePathCanonicalizer.TryCanonicalize(path);
        return canonical == null || DreamTree.IsUnderAny(canonical, SystemPaths) || Hidden(canonical);
    }

    /// <summary>The index of the corpus as it is now (cheap next to a model call, and a corpus is not changed during a run).</summary>
    public static DreamTreeIndex Of(IDreamCorpus corpus) => Build(corpus);

    public IReadOnlyList<DreamArticle> MembersOf(string path) =>
        MembersByPath.TryGetValue(path, out var list) ? list : [];

    /// <summary>The folders directly below <paramref name="path"/>.</summary>
    public IEnumerable<IndexedFolder> ChildrenOf(string path) =>
        FoldersByPath.Values.Where(f => f.Path != DreamTree.Root && DreamTree.ParentOf(f.Path) == path);

    private static DreamTreeIndex Build(IDreamCorpus corpus)
    {
        var folders = new List<IndexedFolder>();
        var system = new List<string>();
        foreach (var f in corpus.Folders)
        {
            var path = TreePathCanonicalizer.TryCanonicalize(f.Path);
            if (path == null) continue;
            if (f.IsSystem || SystemFolders.IsReservedSystemPath(path)) system.Add(path);
            else if (!corpus.IsHiddenPath(path)) folders.Add(new IndexedFolder(f, path));
        }

        // A folder below a system folder is not offered either, whatever the corpus says about it.
        var usable = new Dictionary<string, IndexedFolder>(StringComparer.Ordinal);
        var usableById = new Dictionary<Guid, IndexedFolder>();
        foreach (var f in folders)
        {
            if (DreamTree.IsUnderAny(f.Path, system)) continue;
            if (usable.TryAdd(f.Path, f)) usableById.TryAdd(f.Id, f);
        }

        var byId = new Dictionary<Guid, DreamArticle>();
        var pathById = new Dictionary<Guid, string>();
        var members = new Dictionary<string, List<DreamArticle>>(StringComparer.Ordinal);
        foreach (var a in corpus.Articles)
        {
            if (!byId.TryAdd(a.Id, a)) continue;
            var path = TreePathCanonicalizer.TryCanonicalize(a.TreePath) ?? DreamTree.Root;
            if (DreamTree.IsUnderAny(path, system) || corpus.IsHiddenPath(path)) continue;
            pathById[a.Id] = path;
            if (!members.TryGetValue(path, out var list)) members[path] = list = [];
            list.Add(a);
        }

        return new DreamTreeIndex
        {
            ById = byId,
            FoldersByPath = usable,
            FoldersById = usableById,
            MembersByPath = members,
            SystemPaths = system,
            PathById = pathById,
            Hidden = corpus.IsHiddenPath,
        };
    }
}

/// <summary>Path and text helpers shared by folder_review and tree_review.</summary>
internal static class DreamTree
{
    public const string Root = "/";

    /// <summary>A folder name the model proposes is at most this long (the product allows 255; a good name is short).</summary>
    public const int MaxNameLength = 80;

    // The elements the two inputs are made of. A note, a title or a folder name must not be able to close one of them, or
    // open another, and so step out of the data: the opening angle bracket of such a tag is written as an entity.
    private static readonly Regex InputTag = new(@"<(\s*/?\s*(?:note|folder|tree))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>The path of the folder that holds <paramref name="path"/>; null for the top level.</summary>
    public static string? ParentOf(string path)
    {
        if (path == Root) return null;
        var i = path.LastIndexOf('/');
        return i <= 0 ? Root : path[..i];
    }

    public static string NameOf(string path) => path == Root ? "" : path[(path.LastIndexOf('/') + 1)..];

    public static int DepthOf(string path) => path == Root ? 0 : path.Count(c => c == '/');

    public static string Join(string parent, string name) => parent == Root ? "/" + name : parent + "/" + name;

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="ancestor"/>.</summary>
    public static bool IsUnder(string path, string ancestor) =>
        ancestor == Root ? path != Root : path.Length > ancestor.Length && path.StartsWith(ancestor, StringComparison.Ordinal)
            && path[ancestor.Length] == '/';

    public static bool IsUnderOrSame(string path, string ancestor) => path == ancestor || IsUnder(path, ancestor);

    public static bool IsUnderAny(string path, IEnumerable<string> ancestors) => ancestors.Any(a => IsUnderOrSame(path, a));

    /// <summary>A sort key that puts a folder right before the folders inside it ("/a" before "/a/b" before "/a b").</summary>
    public static string TreeOrderKey(string path) => path.Replace('/', '\u0001');

    /// <summary>
    /// A name for one folder from a model: cleaned, not empty, no slash, not "." or "..", at most
    /// <see cref="MaxNameLength"/> characters. Too long is a refusal, not a cut: half a name is a different name.
    /// </summary>
    public static string? CleanSegment(string? raw)
    {
        if (raw == null) return null;
        var name = DreamJsonReader.CleanText(raw, MaxNameLength + 1);
        if (name.Length == 0 || name.Length > MaxNameLength) return null;
        if (name is "." or ".." || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)) return null;
        return name;
    }

    /// <summary>Text from the knowledge base made fit for one line of the input.</summary>
    public static string Line(string text, int max) => Neutralize(DreamJsonReader.CleanText(text, max));

    public static string Neutralize(string text)
    {
        try
        {
            return InputTag.Replace(text, "&lt;$1");
        }
        catch (RegexMatchTimeoutException)
        {
            return text.Replace("<", "&lt;", StringComparison.Ordinal);
        }
    }

    /// <summary>The first <paramref name="count"/> characters, never ending between the halves of a surrogate pair.</summary>
    public static string Head(string text, int count)
    {
        if (text.Length <= count) return text;
        if (count > 0 && char.IsHighSurrogate(text[count - 1])) count--;
        return text[..count];
    }
}

/// <summary>
/// The folder paths a model may answer with, matched against what the input listed. The input shows a path cleaned of
/// control characters and doubled spaces, so both sides are compared in that form. A path from the model is canonicalised
/// first; it is then looked up exactly (paths are case sensitive), and failing that by case, but only when exactly one
/// listed path matches. The answer is always the listed path itself.
/// </summary>
internal sealed class DreamPathSet
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _byLower = new(StringComparer.Ordinal);

    public DreamPathSet(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var key = Shown(p);
            if (!_exact.TryAdd(key, p)) continue;
            var lower = key.ToLowerInvariant();
            if (!_byLower.TryGetValue(lower, out var list)) _byLower[lower] = list = [];
            list.Add(p);
        }
    }

    /// <summary>The listed path a model's text means, or null.</summary>
    public string? Match(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = TreePathCanonicalizer.TryCanonicalize(DreamJsonReader.CleanText(raw, 2000));
        if (path == null) return null;
        if (_exact.TryGetValue(path, out var listed)) return listed;
        return _byLower.TryGetValue(path.ToLowerInvariant(), out var list) && list.Count == 1 ? list[0] : null;
    }

    private static string Shown(string path) => DreamJsonReader.CleanText(path, 4000);
}
