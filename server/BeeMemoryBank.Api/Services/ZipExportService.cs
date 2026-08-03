using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Api.Helpers;

namespace BeeMemoryBank.Api.Services;

public partial class ZipExportService(
    ArticleService articleService,
    MediaService mediaService,
    ConceptTagService conceptTagService,
    IFolderRepository folderRepo,
    CallerScopeHolder scopeHolder)
{
    private ICallerScope Scope => scopeHolder.Scope;

    private static readonly JsonSerializerOptions ManifestJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Marker file dropped into a folder that has no articles anywhere in its subtree,
    /// so the folder still physically exists in the ZIP (a flat file listing can't otherwise
    /// represent an empty directory) and survives a plain extract-and-browse too.</summary>
    private const string EmptyFolderMarkerName = ".bmb-keep";

    private const string ManifestEntryName = ".bmb-manifest.json";

    // Plaintext markdown export can't include a password-protected body (no passphrase here, and the
    // raw BMBENC1 ciphertext would just be confusing base64). Write a placeholder instead.
    private const string ProtectedExportNotice =
        "🔒 This article is password-protected (second-layer encryption) and was not included in this export.\n";

    private static readonly string TempBase = Path.Combine(Path.GetTempPath(), "bmb-downloads");

    private static string EnsureTempDir()
    {
        Directory.CreateDirectory(TempBase);
        return TempBase;
    }

    private static string NewTempPath() => Path.Combine(EnsureTempDir(), Guid.NewGuid().ToString("N") + ".tmp");

    public async Task<(string filePath, string fileName)> ExportArticleAsync(Guid articleId, bool withImages, CancellationToken ct)
    {
        var article = await articleService.GetMetadataAsync(articleId)
            ?? throw new KeyNotFoundException($"Article {articleId} not found.");

        if (Scope.IsAccessDenied(article.TreePath))
            throw new UnauthorizedAccessException("You don't have permission to access this article.");

        var slug = FileNameHelper.SanitizeFileName(article.Title);
        var content = await articleService.GetContentAsync(articleId);
        if (BeeMemoryBank.Crypto.ProtectedContentCodec.IsProtected(content))
            content = ProtectedExportNotice;

        var mediaList = withImages ? await mediaService.GetByArticleIdAsync(articleId) : [];

        if (mediaList.Count == 0 || !withImages)
        {
            var mdPath = NewTempPath();
            await File.WriteAllTextAsync(mdPath, content, Encoding.UTF8, ct);
            return (mdPath, $"{slug}.md");
        }

        var zipPath = NewTempPath();
        
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mediaMap = new Dictionary<Guid, string>();
        foreach (var m in mediaList)
            mediaMap[m.Id] = GetUniqueName(usedFileNames, m.FileName);

        var rewritten = RewriteMediaRefs(content, "attachments", mediaMap);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8))
        {
            var mdEntry = zip.CreateEntry($"{slug}.md", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(mdEntry.Open(), Encoding.UTF8))
                await writer.WriteAsync(rewritten.AsMemory(), ct);

            foreach (var m in mediaList)
            {
                ct.ThrowIfCancellationRequested();
                var mediaContent = await mediaService.GetContentAsync(m.Id);
                if (mediaContent == null) continue;

                var imageEntry = zip.CreateEntry($"attachments/{mediaMap[m.Id]}", CompressionLevel.Optimal);
                using var imageStream = imageEntry.Open();
                await imageStream.WriteAsync(mediaContent.Value.data, ct);
            }
        }

        return (zipPath, $"{slug}.zip");
    }

    public async Task<(string filePath, string fileName)> ExportFolderAsync(string path, bool withImages, CancellationToken ct)
    {
        path = path.TrimEnd('/');
        var allArticles = await articleService.ListAsync(path);
        var filtered = Scope.FilterArticles(allArticles);

        // Folders (including empty ones) are exported independently of whether the folder has
        // any articles - a folder with zero articles anywhere in its subtree is still a valid,
        // non-empty export target as long as IT ITSELF is a real folder.
        var folders = await GetFoldersInScopeAsync(path);
        if (filtered.Count == 0 && folders.Count == 0)
            throw new ArgumentException("Folder is empty");

        var folderName = path.Split('/').LastOrDefault("folder");
        var zipPath = NewTempPath();

        await BuildZipAsync(zipPath, filtered, folders, withImages, path, folderName, ct);

        return (zipPath, $"{FileNameHelper.SanitizeFileName(folderName)}.zip");
    }

    public async Task<(string filePath, string fileName)> ExportAllAsync(bool withImages, CancellationToken ct)
    {
        var allArticles = await articleService.ListAsync();
        var filtered = Scope.FilterArticles(allArticles);
        var folders = await GetFoldersInScopeAsync("");

        if (filtered.Count == 0 && folders.Count == 0)
            throw new ArgumentException("Nothing to export");

        var zipPath = NewTempPath();
        var dateStamp = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // No single folder identity to preserve for a root/"export all" export.
        await BuildZipAsync(zipPath, filtered, folders, withImages, "", sourceFolderName: null, ct);

        return (zipPath, $"BeeMemoryBank-{dateStamp}.zip");
    }

    /// <summary>All active, ACL-visible folders at <paramref name="rootPath"/> or nested under it.</summary>
    private async Task<List<Folder>> GetFoldersInScopeAsync(string rootPath)
    {
        var all = Scope.FilterFolders(await folderRepo.GetAllActiveAsync());
        if (rootPath.Length == 0) return all;
        return all.Where(f => f.Path == rootPath || f.Path.StartsWith(rootPath + "/", StringComparison.Ordinal)).ToList();
    }

    private async Task BuildZipAsync(
        string zipPath, List<Article> articles, List<Folder> folders, bool withImages,
        string rootPath, string? sourceFolderName, CancellationToken ct)
    {
        var slugTracker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mdEntryUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var usedAttachmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalMediaMap = new Dictionary<Guid, string>(); // MediaId -> finalFilename
        var writtenMediaIds = new HashSet<Guid>();

        var manifestArticles = new List<BeeExportManifestArticle>();
        // Relative dir of every article that actually landed in the zip - used below to tell
        // which manifested folders are genuinely empty (no article anywhere in their subtree)
        // and therefore need an explicit marker to survive the round trip.
        var articleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8);
        foreach (var article in articles)
        {
            ct.ThrowIfCancellationRequested();
            var (_, mdFileName) = GetUniqueSlug(slugTracker, mdEntryUsed, article.Title, article.TreePath, rootPath);
            articleDirs.Add(GetDirOf(mdFileName));

            var content = await articleService.GetContentAsync(article.Id);
            var isProtected = BeeMemoryBank.Crypto.ProtectedContentCodec.IsProtected(content);
            if (isProtected)
                content = ProtectedExportNotice;
            var mediaList = withImages ? await mediaService.GetByArticleIdAsync(article.Id) : [];

            string rewritten;
            if (withImages && mediaList.Count > 0)
            {
                foreach (var m in mediaList)
                {
                    if (!globalMediaMap.ContainsKey(m.Id))
                        globalMediaMap[m.Id] = GetUniqueName(usedAttachmentNames, m.FileName);
                }

                var articleMap = mediaList.ToDictionary(m => m.Id, m => globalMediaMap[m.Id]);
                rewritten = RewriteMediaRefs(content, "attachments", articleMap);

                foreach (var m in mediaList)
                {
                    if (writtenMediaIds.Add(m.Id))
                    {
                        var mediaContent = await mediaService.GetContentAsync(m.Id);
                        if (mediaContent == null) continue;

                        var entry = zip.CreateEntry($"attachments/{globalMediaMap[m.Id]}", CompressionLevel.Optimal);
                        using var s = entry.Open();
                        await s.WriteAsync(mediaContent.Value.data, ct);
                    }
                }
            }
            else
            {
                rewritten = content;
            }

            var mdEntry = zip.CreateEntry(mdFileName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(mdEntry.Open(), Encoding.UTF8);
            await writer.WriteAsync(rewritten.AsMemory(), ct);

            var tags = await conceptTagService.GetByArticleIdAsync(article.Id);
            manifestArticles.Add(new BeeExportManifestArticle
            {
                File = mdFileName,
                Title = article.Title,
                Tags = tags,
                CreatedAt = article.CreatedAt,
                UpdatedAt = article.UpdatedAt,
                Protected = isProtected
            });
        }

        var manifestFolders = new List<string>();
        foreach (var folder in folders)
        {
            var relPath = rootPath.Length > 0 ? folder.Path[rootPath.Length..].TrimStart('/') : folder.Path.TrimStart('/');
            manifestFolders.Add(relPath);

            // Empty iff no article's directory equals this folder or descends from it.
            var isEmpty = !articleDirs.Any(dir =>
                string.Equals(dir, relPath, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(relPath + "/", StringComparison.OrdinalIgnoreCase));
            if (isEmpty)
            {
                var markerPath = relPath.Length > 0 ? $"{relPath}/{EmptyFolderMarkerName}" : EmptyFolderMarkerName;
                zip.CreateEntry(markerPath, CompressionLevel.NoCompression);
            }
        }

        var manifest = new BeeExportManifest
        {
            ExportedAt = DateTime.UtcNow,
            SourceFolderName = sourceFolderName,
            Folders = manifestFolders,
            Articles = manifestArticles
        };
        var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        using (var manifestWriter = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            await manifestWriter.WriteAsync(JsonSerializer.Serialize(manifest, ManifestJsonOpts).AsMemory(), ct);
    }

    /// <summary>Directory portion of a zip-relative file path ("" if the file sits at the root).</summary>
    private static string GetDirOf(string zipRelativePath)
    {
        var lastSlash = zipRelativePath.LastIndexOf('/');
        return lastSlash > 0 ? zipRelativePath[..lastSlash] : "";
    }

    private static (string slug, string mdFileName) GetUniqueSlug(
        Dictionary<string, int> slugTracker, HashSet<string> mdEntryUsed, string title, string treePath, string rootPath)
    {
        var slug = FileNameHelper.SanitizeFileName(title);
        var basePath = rootPath.Length > 0 ? treePath[rootPath.Length..].TrimStart('/') : treePath.TrimStart('/');
        var folder = basePath.Length > 0 ? basePath + "/" : "";

        var mdFileName = folder + slug + ".md";
        while (!mdEntryUsed.Add(mdFileName))
        {
            slugTracker.TryGetValue(slug, out var count);
            count++;
            slugTracker[slug] = count;
            mdFileName = folder + $"{slug} ({count}).md";
        }
        slugTracker.TryAdd(slug, 1);
        return (slug, mdFileName);
    }

    private static string GetUniqueName(HashSet<string> used, string original)
    {
        var name = original;
        if (!used.Contains(name))
        {
            used.Add(name);
            return name;
        }

        var baseName = Path.GetFileNameWithoutExtension(original);
        var ext = Path.GetExtension(original);
        var counter = 2;
        while (used.Contains($"{baseName} ({counter}){ext}"))
            counter++;

        var result = $"{baseName} ({counter}){ext}";
        used.Add(result);
        return result;
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\(/api/media/([0-9a-fA-F-]{36})\)")]
    private static partial Regex MediaRefRegex();

    private static string RewriteMediaRefs(string content, string imageFolderEncoded, Dictionary<Guid, string> mediaMap)
    {
        return MediaRefRegex().Replace(content, match =>
        {
            var alt = match.Groups[1].Value;
            var mediaIdStr = match.Groups[2].Value;
            if (!Guid.TryParse(mediaIdStr, out var mediaId) || !mediaMap.TryGetValue(mediaId, out var fileName))
                return match.Value;
            var encodedFileName = Uri.EscapeDataString(fileName);
            return $"![{alt}]({imageFolderEncoded}/{encodedFileName})";
        });
    }
}
