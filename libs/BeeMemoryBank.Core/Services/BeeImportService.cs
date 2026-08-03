using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Imports a ZIP produced by <c>ZipExportService</c> (Export All / Export Folder) into a chosen
/// destination, restoring what a flat file listing can't represent on its own: the original
/// folder's own name, empty folders, exact article titles/tags, and password-protected articles
/// (skipped with a warning instead of silently creating a placeholder-text article).
///
/// Deliberately separate from <see cref="ObsidianImportService"/>: that one is a best-effort
/// importer for THIRD-PARTY Obsidian vaults (always lands under an auto-named
/// "Imported from Obsidian (date)" folder, titles/tags are guesses). This one requires the
/// ".bmb-manifest.json" sidecar it expects to find and refuses anything that doesn't have it,
/// rather than silently degrading — a BeeMemoryBank-to-BeeMemoryBank transfer should be exact,
/// not best-effort.
/// </summary>
public partial class BeeImportService(
    ArticleService articleService,
    MediaService mediaService,
    IFolderRepository folderRepo,
    INodeIdentityRepository nodeRepo)
{
    private const string ManifestEntryName = ".bmb-manifest.json";

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

    private static readonly JsonSerializerOptions ManifestJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ZipExportService only ever writes plain markdown image syntax (never Obsidian's
    // ![[wiki-embed]] form) - one regex is enough here, unlike ObsidianImportService.
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex MarkdownImageRegex();

    public async Task<BeeImportReport> ImportAsync(Stream zipStream, string destinationPath, CancellationToken ct)
    {
        var report = new BeeImportReport();

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = archive.GetEntry(ManifestEntryName);
        if (manifestEntry == null)
        {
            throw new InvalidOperationException(
                "This ZIP doesn't look like a BeeMemoryBank export (no .bmb-manifest.json found). " +
                "Use \"Import from Obsidian\" for Obsidian vaults instead.");
        }

        BeeExportManifest manifest;
        using (var stream = manifestEntry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var json = await reader.ReadToEndAsync(ct);
            manifest = JsonSerializer.Deserialize<BeeExportManifest>(json, ManifestJsonOpts)
                ?? throw new InvalidOperationException("Could not parse .bmb-manifest.json.");
        }

        destinationPath = TreePathCanonicalizer.Canonicalize(
            string.IsNullOrWhiteSpace(destinationPath) ? "/" : destinationPath);

        var rootPath = !string.IsNullOrEmpty(manifest.SourceFolderName)
            ? JoinPath(destinationPath, await ResolveFolderNameCollision(manifest.SourceFolderName, destinationPath))
            : destinationPath;
        report.RootFolderPath = rootPath;

        var identity = await nodeRepo.GetAsync();

        // Create every manifested folder up front, shortest path first, so EMPTY folders (the
        // whole reason this list exists) survive even though no article will ever touch them.
        // EnsureExistsAsync recursively creates missing ancestors and is a no-op if the path
        // already exists, so order beyond "shortest first" doesn't matter and this is safe to
        // run even for folders an article will also independently vivify below.
        foreach (var relFolder in manifest.Folders.OrderBy(f => f.Length))
        {
            ct.ThrowIfCancellationRequested();
            var folderPath = relFolder.Length > 0 ? JoinPath(rootPath, relFolder) : rootPath;
            var existedBefore = await folderRepo.GetByPathAsync(folderPath) != null;
            await folderRepo.EnsureExistsAsync(folderPath, identity?.NodeId);
            if (!existedBefore) report.FoldersCreated++;
        }

        var imageIndex = BuildImageIndex(archive.Entries.ToList(), report);

        foreach (var manifestArticle in manifest.Articles)
        {
            ct.ThrowIfCancellationRequested();

            if (manifestArticle.Protected)
            {
                report.ArticlesSkippedProtected++;
                report.Warnings.Add(
                    $"Skipped password-protected article '{manifestArticle.Title}' - it was exported " +
                    "as a placeholder, not its real content. Unlock it and re-export to bring it over.");
                continue;
            }

            var entry = archive.GetEntry(manifestArticle.File);
            if (entry == null)
            {
                report.Warnings.Add($"Manifest referenced '{manifestArticle.File}' but it's missing from the ZIP.");
                continue;
            }

            string body;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }

            var relDir = GetDirOf(manifestArticle.File);
            var treePath = relDir.Length > 0 ? JoinPath(rootPath, relDir) : rootPath;

            var uploadedByEntryPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var rewritten = await RewriteImageRefsAsync(body, imageIndex, uploadedByEntryPath, report, ct);

            try
            {
                await articleService.CreateAsync(manifestArticle.Title, treePath, manifestArticle.Tags, rewritten);
                report.ArticlesCreated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Warnings.Add($"Failed to import '{manifestArticle.File}': {ex.Message}");
            }
        }

        return report;
    }

    private static string GetDirOf(string zipRelativePath)
    {
        var lastSlash = zipRelativePath.LastIndexOf('/');
        return lastSlash > 0 ? zipRelativePath[..lastSlash] : "";
    }

    private static string JoinPath(string parent, string child)
    {
        var basePath = parent == "/" ? "" : parent.TrimEnd('/');
        return $"{basePath}/{child.Trim('/')}";
    }

    private async Task<string> ResolveFolderNameCollision(string folderName, string targetParentPath)
    {
        var siblings = await folderRepo.GetChildrenAsync(targetParentPath == "/" ? null : targetParentPath);
        var taken = siblings.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(folderName)) return folderName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{folderName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Too many name collisions at the destination folder.");
    }

    private static Dictionary<string, ZipArchiveEntry> BuildImageIndex(List<ZipArchiveEntry> entries, BeeImportReport report)
    {
        var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
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

    private async Task<string> RewriteImageRefsAsync(
        string body,
        Dictionary<string, ZipArchiveEntry> imageIndex,
        Dictionary<string, Guid> uploadedByEntryPath,
        BeeImportReport report,
        CancellationToken ct)
    {
        var replacements = new List<(Match match, string replacement)>();

        foreach (Match match in MarkdownImageRegex().Matches(body))
        {
            var alt = match.Groups[1].Value;
            var path = match.Groups[2].Value;
            var fileName = Uri.UnescapeDataString(Path.GetFileName(path));
            var effectiveAlt = string.IsNullOrEmpty(alt) ? Path.GetFileNameWithoutExtension(fileName) : alt;
            var replacement = await ProcessImageAsync(fileName, effectiveAlt, imageIndex, uploadedByEntryPath, report, ct);
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

    private async Task<string> ProcessImageAsync(
        string fileName,
        string alt,
        Dictionary<string, ZipArchiveEntry> imageIndex,
        Dictionary<string, Guid> uploadedByEntryPath,
        BeeImportReport report,
        CancellationToken ct)
    {
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

            var ext = Path.GetExtension(fileName);
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
