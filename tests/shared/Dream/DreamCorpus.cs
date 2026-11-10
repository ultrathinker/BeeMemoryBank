using System.Reflection;
using System.Text.Json;

using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// One article of the Dream corpus (package X1). <paramref name="Path"/> is the folder the
/// article lives in; <paramref name="Title"/>, <paramref name="Tags"/> and
/// <paramref name="Body"/> are exact. A non-null <paramref name="ProtectedPassphrase"/> makes
/// the seeder protect the article right after creating it.
/// </summary>
public sealed record DreamCorpusArticle(
    string Path,
    string Title,
    IReadOnlyList<string> Tags,
    string Body,
    string? ProtectedPassphrase = null);

/// <summary>One object reference inside an <c>expected[]</c> entry.</summary>
public sealed record DreamCorpusExpectedObject(
    string? Role = null,
    string? Folder = null,
    string? Title = null,
    string? Tag = null);

/// <summary>
/// One expected detector finding: the rule kind, the objects it names and the score section 3.6
/// of the dream plan assigns. Where the score is a formula constant the entry carries
/// <see cref="Score"/>; where the formula derives it from a measured similarity the entry carries
/// the clamp bounds <see cref="ScoreMin"/>/<see cref="ScoreMax"/>.
/// </summary>
public sealed record DreamCorpusExpected(
    string Kind,
    IReadOnlyList<DreamCorpusExpectedObject> Objects,
    int? Score = null,
    int? ScoreMin = null,
    int? ScoreMax = null,
    string? Note = null);

/// <summary>The whole dream-corpus.json definition.</summary>
public sealed record DreamCorpusDefinition(
    int Version,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Folders,
    IReadOnlyList<DreamCorpusArticle> Articles,
    IReadOnlyList<string> UnusedTags,
    IReadOnlyList<DreamCorpusExpected> Expected)
{
    /// <summary>Where the definition was loaded from; not part of the JSON.</summary>
    public string Source { get; init; } = "<embedded>";
}

/// <summary>An article the seeder protected with a passphrase.</summary>
public sealed record DreamCorpusProtectedArticle(Guid Id, string FolderPath, string Title, string Passphrase);

/// <summary>
/// The corpus seeded onto a live node: everything the definition names, with ids. Lookups throw
/// rather than return null so a stale expected[] entry fails loudly instead of silently.
/// </summary>
public sealed class DreamCorpusSeeded
{
    private readonly Dictionary<(string FolderPath, string Title), Guid> _articleIds;
    private readonly Dictionary<string, List<Guid>> _idsByTitle;

    public DreamCorpusSeeded(
        DreamCorpusDefinition definition,
        IReadOnlyDictionary<string, Guid> folderIdByPath,
        IReadOnlyList<DreamCorpusProtectedArticle> protectedArticles,
        Dictionary<(string FolderPath, string Title), Guid> articleIds)
    {
        Definition = definition;
        FolderIdByPath = folderIdByPath;
        ProtectedArticles = protectedArticles;
        _articleIds = articleIds;
        _idsByTitle = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var ((folderPath, title), id) in articleIds)
        {
            if (!_idsByTitle.TryGetValue(title, out var ids))
                _idsByTitle[title] = ids = [];
            ids.Add(id);
        }
    }

    public DreamCorpusDefinition Definition { get; }

    /// <summary>Every declared folder's id, by its exact path.</summary>
    public IReadOnlyDictionary<string, Guid> FolderIdByPath { get; }

    /// <summary>The articles seeded with a <c>protectedPassphrase</c>.</summary>
    public IReadOnlyList<DreamCorpusProtectedArticle> ProtectedArticles { get; }

    public int ArticleCount => _articleIds.Count;

    public bool TryArticleId(string folderPath, string title, out Guid id) =>
        _articleIds.TryGetValue((folderPath, title), out id);

    /// <summary>The id of the corpus article seeded into <paramref name="folderPath"/> with <paramref name="title"/>.</summary>
    public Guid ArticleId(string folderPath, string title) =>
        TryArticleId(folderPath, title, out var id)
            ? id
            : throw new KeyNotFoundException($"The corpus has no article titled '{title}' in '{folderPath}'.");

    /// <summary>
    /// The id of the only article with this exact title across the whole corpus. Throws for a
    /// title the corpus plants twice (the exact-duplicate pairs); look those up by folder and
    /// title instead; that ambiguity is the point of those pairs.
    /// </summary>
    public Guid SoleArticleId(string title)
    {
        if (!_idsByTitle.TryGetValue(title, out var ids))
            throw new KeyNotFoundException($"The corpus has no article titled '{title}'.");
        if (ids.Count > 1)
            throw new InvalidOperationException(
                $"'{title}' is planted {ids.Count} times in the corpus (an exact-duplicate pair); look it up by folder and title instead.");
        return ids[0];
    }
}

/// <summary>
/// Loader and seeder for the Dream corpus (package X1): about 120 short, plausible notes with
/// planted defects, the calibration and regression material for the dream rule detectors. The
/// JSON ships as an embedded resource named <c>dream-corpus.json</c> in every test assembly that
/// links this file; <see cref="SeedAsync"/> creates the whole corpus on a live, unlocked test node.
/// </summary>
public static class DreamCorpus
{
    private const string ResourceNameSuffix = "dream-corpus.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads the definition: the embedded resource, or the JSON file at <paramref name="path"/>.</summary>
    public static DreamCorpusDefinition Load(string? path = null)
    {
        string json;
        string source;
        if (path != null)
        {
            json = File.ReadAllText(path);
            source = path;
        }
        else
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().SingleOrDefault(n =>
                n.EndsWith(ResourceNameSuffix, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No embedded resource ending with '{ResourceNameSuffix}' in {assembly.GetName().Name}; link dream-corpus.json as an EmbeddedResource.");
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
            source = $"embedded:{name}";
        }

        var definition = JsonSerializer.Deserialize<DreamCorpusDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"The corpus at {source} deserialized to null.");
        if (definition.Articles.Count == 0)
            throw new InvalidOperationException($"The corpus at {source} holds no articles.");
        return definition with { Source = source };
    }

    /// <summary>
    /// Seeds the corpus onto the node behind the DI scope of <paramref name="scopedServiceProvider"/>.
    /// The node must already be initialized and unlocked (a session login in the test does that).
    /// Declared folders are created when missing (parent before child); articles are created in
    /// definition order: each exact-duplicate ORIGINAL comes before its copy, so with no article
    /// references the trash rule's tie-break picks the later-seeded copy; and an article with a
    /// <c>protectedPassphrase</c> is protected right after creation. Each unused tag is attached
    /// to a carrier article and unlinked again, leaving the bare tag row (zero articles) that the
    /// delete_unused_tags rule looks for.
    /// </summary>
    public static async Task<DreamCorpusSeeded> SeedAsync(
        IServiceProvider scopedServiceProvider,
        DreamCorpusDefinition? definition = null,
        CancellationToken cancellationToken = default)
    {
        definition ??= Load();

        var folderRepo = scopedServiceProvider.GetRequiredService<IFolderRepository>();
        var folderService = scopedServiceProvider.GetRequiredService<FolderService>();
        var articleService = scopedServiceProvider.GetRequiredService<ArticleService>();
        var tagService = scopedServiceProvider.GetRequiredService<ConceptTagService>();

        foreach (var path in OrderedFolders(definition))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await folderRepo.GetByPathAsync(path) != null) continue;
            await folderService.CreateAsync(path);
        }

        var articleIds = new Dictionary<(string FolderPath, string Title), Guid>();
        var protectedArticles = new List<DreamCorpusProtectedArticle>();
        Article? carrier = null;
        foreach (var a in definition.Articles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var article = await articleService.CreateAsync(a.Title, a.Path, new List<string>(a.Tags), a.Body);
            articleIds[(a.Path, a.Title)] = article.Id;
            if (a.ProtectedPassphrase is { } passphrase)
            {
                await articleService.ProtectAsync(article.Id, passphrase, hint: null);
                protectedArticles.Add(new DreamCorpusProtectedArticle(article.Id, a.Path, a.Title, passphrase));
            }
            else
            {
                carrier ??= article;
            }
        }

        if (definition.UnusedTags.Count > 0)
        {
            if (carrier == null)
                throw new InvalidOperationException(
                    "The corpus defines unused tags but no unprotected article to carry them.");
            foreach (var tag in definition.UnusedTags)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await articleService.AddTagsAsync(carrier.Id, [tag]);
                await articleService.RemoveTagAsync(carrier.Id, tag);
            }
        }

        var folderIdByPath = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var path in definition.Folders)
        {
            var folder = await folderRepo.GetByPathAsync(path)
                ?? throw new InvalidOperationException($"Folder '{path}' did not exist after seeding.");
            folderIdByPath[path] = folder.Id;
        }

        return new DreamCorpusSeeded(definition, folderIdByPath, protectedArticles, articleIds);
    }

    /// <summary>Shallowest first, then by name: parents are created before children, deterministically.</summary>
    private static IEnumerable<string> OrderedFolders(DreamCorpusDefinition definition) =>
        definition.Folders
            .OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.Ordinal);
}
