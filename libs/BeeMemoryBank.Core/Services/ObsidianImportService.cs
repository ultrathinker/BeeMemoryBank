using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public partial class ObsidianImportService(
    ArticleService articleService,
    MediaService mediaService)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"
    };

    private static readonly Dictionary<string, string> ExtensionToContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml"
    };

    [GeneratedRegex(@"!\[\[([^\]|]+?)(?:\|[^\]]*)?\]\]")]
    private static partial Regex ObsidianEmbedRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex MarkdownImageRegex();

    public async Task<ObsidianImportReport> ImportAsync(Stream zipStream, CancellationToken ct)
    {
        var report = new ObsidianImportReport();

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var entries = archive.Entries
            .Where(e => !ShouldSkip(Normalize(e.FullName)))
            .ToList();

        var topDir = DetectTopLevelDir(entries);
        var rootPath = $"/Imported from Obsidian ({DateTime.Now:yyyy-MM-dd HH:mm})";
        report.RootFolderPath = rootPath;

        var imageIndex = BuildImageIndex(entries, report);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Normalize(entry.FullName);
            if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length == 0) continue;

            var relativeDir = GetRelativeDir(normalized, topDir);
            var treePath = relativeDir.Length > 0 ? $"{rootPath}/{relativeDir}" : rootPath;

            try
            {
                string body;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync(ct);
                }

                var stripped = StripFrontmatter(body, out var rawFrontmatter);
                var title = ExtractTitle(rawFrontmatter) ?? Path.GetFileNameWithoutExtension(entry.Name);

                var perArticleUploaded = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                var rewritten = await RewriteEmbedsAsync(stripped, imageIndex, perArticleUploaded, report, ct);

                await articleService.CreateAsync(title, treePath, [], rewritten);
                report.ArticlesCreated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Warnings.Add($"Failed to import '{normalized}': {ex.Message}");
            }
        }

        return report;
    }

    private static string Normalize(string fullName) => fullName.Replace('\\', '/');

    private static Dictionary<string, ZipArchiveEntry> BuildImageIndex(
        List<ZipArchiveEntry> entries, ObsidianImportReport report)
    {
        var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(Normalize(entry.FullName));
            if (string.IsNullOrEmpty(name)) continue;
            var ext = Path.GetExtension(name);
            if (!ImageExtensions.Contains(ext)) continue;

            if (index.ContainsKey(name))
            {
                report.Warnings.Add($"Duplicate image filename '{name}'; using first occurrence.");
                continue;
            }

            index[name] = entry;
        }
        return index;
    }

    private static bool ShouldSkip(string fullPath)
    {
        var segments = fullPath.Split('/');
        foreach (var seg in segments)
        {
            if (seg.Equals(".obsidian", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.StartsWith('.')) return true;
        }

        if (fullPath.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? DetectTopLevelDir(List<ZipArchiveEntry> entries)
    {
        string? commonTop = null;
        foreach (var entry in entries)
        {
            var name = Normalize(entry.FullName);
            var slashIdx = name.IndexOf('/');
            if (slashIdx < 0) return null;
            var top = name[..slashIdx];
            if (commonTop == null)
                commonTop = top;
            else if (commonTop != top)
                return null;
        }
        return commonTop;
    }

    private static string GetRelativeDir(string fullName, string? topDir)
    {
        var path = fullName;
        if (topDir != null && path.StartsWith(topDir + "/"))
            path = path[(topDir.Length + 1)..];

        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : "";
    }

    private static string StripFrontmatter(string body, out string? rawFrontmatter)
    {
        rawFrontmatter = null;
        if (!body.StartsWith("---\n") && !body.StartsWith("---\r\n")) return body;

        var sep = body.StartsWith("---\r\n") ? "\r\n" : "\n";
        var contentStart = 3 + sep.Length;
        var closeIdx = body.IndexOf(sep + "---", contentStart, StringComparison.Ordinal);
        if (closeIdx < 0) return body;

        var afterMarker = closeIdx + sep.Length + 3;
        if (afterMarker < body.Length && body[afterMarker] != '\n' && body[afterMarker] != '\r')
            return body;

        rawFrontmatter = body[contentStart..closeIdx];
        var bodyStart = afterMarker;
        if (bodyStart < body.Length && body[bodyStart] == '\r') bodyStart++;
        if (bodyStart < body.Length && body[bodyStart] == '\n') bodyStart++;
        return body[bodyStart..];
    }

    private static string? ExtractTitle(string? frontmatter)
    {
        if (frontmatter == null) return null;
        var match = Regex.Match(frontmatter, @"^title:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private async Task<string> RewriteEmbedsAsync(
        string body,
        Dictionary<string, ZipArchiveEntry> imageIndex,
        Dictionary<string, Guid> uploadedByEntryPath,
        ObsidianImportReport report,
        CancellationToken ct)
    {
        var replacements = new List<(Match match, string replacement)>();

        foreach (Match match in ObsidianEmbedRegex().Matches(body))
        {
            var raw = match.Groups[1].Value.Trim();
            var fileName = Path.GetFileName(raw.Replace('\\', '/'));
            var alt = Path.GetFileNameWithoutExtension(fileName);
            var replacement = await ProcessEmbedAsync(fileName, alt, imageIndex, uploadedByEntryPath, report, ct);
            replacements.Add((match, replacement));
        }

        foreach (Match match in MarkdownImageRegex().Matches(body))
        {
            var alt = match.Groups[1].Value;
            var path = match.Groups[2].Value;
            var fileName = Uri.UnescapeDataString(Path.GetFileName(path));
            var effectiveAlt = string.IsNullOrEmpty(alt) ? Path.GetFileNameWithoutExtension(fileName) : alt;
            var replacement = await ProcessEmbedAsync(fileName, effectiveAlt, imageIndex, uploadedByEntryPath, report, ct);
            replacements.Add((match, replacement));
        }

        if (replacements.Count == 0) return body;

        var sb = new StringBuilder(body.Length);
        var lastIdx = 0;
        foreach (var (match, replacement) in replacements.OrderBy(r => r.match.Index))
        {
            sb.Append(body, lastIdx, match.Index - lastIdx);
            sb.Append(replacement);
            lastIdx = match.Index + match.Length;
        }
        sb.Append(body, lastIdx, body.Length - lastIdx);

        return sb.ToString();
    }

    private async Task<string> ProcessEmbedAsync(
        string fileName,
        string alt,
        Dictionary<string, ZipArchiveEntry> imageIndex,
        Dictionary<string, Guid> uploadedByEntryPath,
        ObsidianImportReport report,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName);
        if (!ImageExtensions.Contains(ext))
        {
            report.FilesSkipped++;
            return $"[файл не импортирован: {fileName}]";
        }

        if (!imageIndex.TryGetValue(fileName, out var imageEntry))
        {
            report.Warnings.Add($"Image not found in zip: {fileName}");
            return $"[image not found: {fileName}]";
        }

        if (!uploadedByEntryPath.TryGetValue(imageEntry.FullName, out var mediaId))
        {
            byte[] bytes;
            using (var stream = imageEntry.Open())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }

            var contentType = ExtensionToContentType.GetValueOrDefault(ext, "image/png");

            try
            {
                var media = await mediaService.CreateAsync(fileName, contentType, bytes, articleId: null);
                mediaId = media.Id;
                uploadedByEntryPath[imageEntry.FullName] = mediaId;
                report.ImagesImported++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Warnings.Add($"Image rejected ({fileName}): {ex.Message}");
                return $"[image not imported: {fileName}]";
            }
        }

        return $"![{alt}](/api/media/{mediaId})";
    }
}
