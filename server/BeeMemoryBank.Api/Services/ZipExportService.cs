using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Api.Helpers;

namespace BeeMemoryBank.Api.Services;

public partial class ZipExportService(
    ArticleService articleService,
    MediaService mediaService,
    CallerScopeHolder scopeHolder)
{
    private ICallerScope Scope => scopeHolder.Scope;

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

        if (filtered.Count == 0)
            throw new ArgumentException("Folder is empty");

        var folderName = path.Split('/').LastOrDefault("folder");
        var zipPath = NewTempPath();

        await BuildZipAsync(zipPath, filtered, withImages, path, ct);

        return (zipPath, $"{FileNameHelper.SanitizeFileName(folderName)}.zip");
    }

    public async Task<(string filePath, string fileName)> ExportAllAsync(bool withImages, CancellationToken ct)
    {
        var allArticles = await articleService.ListAsync();
        var filtered = Scope.FilterArticles(allArticles);

        if (filtered.Count == 0)
            throw new ArgumentException("Nothing to export");

        var zipPath = NewTempPath();
        var dateStamp = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await BuildZipAsync(zipPath, filtered, withImages, "", ct);

        return (zipPath, $"BeeMemoryBank-{dateStamp}.zip");
    }

    private async Task BuildZipAsync(string zipPath, List<Article> articles, bool withImages, string rootPath, CancellationToken ct)
    {
        var slugTracker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mdEntryUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        var usedAttachmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalMediaMap = new Dictionary<Guid, string>(); // MediaId -> finalFilename
        var writtenMediaIds = new HashSet<Guid>();

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8);
        foreach (var article in articles)
        {
            ct.ThrowIfCancellationRequested();
            var (_, mdFileName) = GetUniqueSlug(slugTracker, mdEntryUsed, article.Title, article.TreePath, rootPath);

            var content = await articleService.GetContentAsync(article.Id);
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
        }
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
