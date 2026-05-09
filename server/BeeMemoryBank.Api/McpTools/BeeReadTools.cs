using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeReadTools(
    ArticleService articleService,
    IArticleVersionRepository versionRepo,
    BeeMemoryBank.Core.Interfaces.IFolderRepository folderRepo,
    SessionService session,
    McpResponseManager responseManager,
    MediaService mediaService,
    IMediaRepository mediaRepo,
    IConceptTagRepository conceptTagRepo)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [McpServerTool(Name = "bee_list_articles")]
    [Description(
        "List articles, optionally filtered by tree path. Soft-deleted articles are not included.\n" +
        "Returns JSON array: [{ id, title, treePath, status, createdAt, updatedAt }]. " +
        "The treePath filter matches articles whose TreePath equals (or is a descendant of) the given path. " +
        "Omit treePath to list everything. For a tree-structured view with empty folders, use bee_get_tree.")]
    public async Task<string> ListArticles(
        [Description("Tree path filter, e.g. '/Work' or '/Work/Dev'. Omit to list all articles.")] string? treePath = null)
    {
        var articles = await articleService.ListAsync(treePath);

        var json = JsonSerializer.Serialize(articles.Select(a => new
        {
            id = a.Id,
            title = a.Title,
            treePath = a.TreePath,
            status = a.Status,
            createdAt = a.CreatedAt,
            updatedAt = a.UpdatedAt
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_get_article")]
    [Description(
        "Get article metadata, and optionally the decrypted body content.\n" +
        "Returns JSON: { id, title, treePath, tags, relatedCount, relatedStrength, createdAt, updatedAt" +
        "[, content] }. 'tags' is a string array of tag names on the article. 'relatedCount' = how many " +
        "other articles share at least one tag with this one; 'relatedStrength' = total sum of shared-tag " +
        "counts across all related articles. Include 'content' only when content=true (saves tokens).\n" +
        "Soft-deleted articles return \"Error: article {id} not found\" — there is no way via MCP to tell " +
        "\"was deleted\" apart from \"never existed\".")]
    public async Task<string> GetArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("If true, includes the decrypted article body as 'content' in the response. Default: false (metadata only, saves tokens).")] bool content = false)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var tags = await conceptTagRepo.GetByArticleIdAsync(id);
        var related = await conceptTagRepo.GetRelatedArticlesAsync(id);
        var relatedCount = related.Count;
        var relatedStrength = related.Sum(r => r.Strength);

        if (content)
        {
            try
            {
                var plaintext = await articleService.GetContentAsync(id);
                var json = JsonSerializer.Serialize(new
                {
                    id = article.Id,
                    title = article.Title,
                    treePath = article.TreePath,
                    tags,
                    relatedCount,
                    relatedStrength,
                    content = plaintext,
                    createdAt = article.CreatedAt,
                    updatedAt = article.UpdatedAt
                }, JsonOpts);
                return responseManager.ProcessResponse(json);
            }
            catch (InvalidOperationException ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        return responseManager.ProcessResponse(JsonSerializer.Serialize(new
        {
            id = article.Id,
            title = article.Title,
            treePath = article.TreePath,
            tags,
            relatedCount,
            relatedStrength,
            createdAt = article.CreatedAt,
            updatedAt = article.UpdatedAt
        }, JsonOpts));
    }

    [McpServerTool(Name = "bee_get_tree")]
    [Description(
        "Get the folder/article tree. Unlike bee_list_articles, this includes empty folders too.\n" +
        "Returns JSON: { paths: [{ path, articles: [{ id, title }] }] }, sorted alphabetically by path. " +
        "Each entry represents one folder and its direct articles (no body/tags — fetch with bee_get_article " +
        "if needed). Use the 'path' parameter to scope the view to one subtree. Soft-deleted folders/articles " +
        "are excluded.")]
    public async Task<string> GetTree(
        [Description("Path filter, e.g. '/Work'. Shows only that folder and its descendants. Omit for the whole tree.")] string? path = null)
    {
        var articles = await articleService.ListAsync(path);
        var folders = await folderRepo.GetAllActiveAsync();

        var articlesByPath = articles
            .GroupBy(a => a.TreePath)
            .ToDictionary(g => g.Key, g => g.Select(a => new { id = a.Id, title = a.Title }).ToList());

        var allPaths = new HashSet<string>(folders.Select(f => f.Path));
        foreach (var a in articles)
            allPaths.Add(a.TreePath);

        var filteredPaths = path != null
            ? allPaths.Where(p => p == path || p.StartsWith(path.TrimEnd('/') + "/"))
            : allPaths;

        var emptyList = new List<object>();
        var byPath = filteredPaths
            .OrderBy(p => p)
            .Select(p => new
            {
                path = p,
                articles = articlesByPath.TryGetValue(p, out var arts)
                    ? arts.Select(a => (object)a).ToList()
                    : emptyList
            });

        var json = JsonSerializer.Serialize(new { paths = byPath }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_get_article_versions")]
    [Description(
        "List version history for an article. Returns metadata only (no content).\n" +
        "Versioning is snapshot-before-write: the CURRENT article content lives on the article itself, " +
        "and each saved version is a snapshot of the state that existed BEFORE some later modification.\n" +
        "Consequence: a freshly created article returns [] until its first edit. " +
        "The first edit creates version 1 (content at creation). Every subsequent update/append/prepend/replace " +
        "adds one version. So N edits → N versions (plus the current state on the article).")]
    public async Task<string> GetArticleVersions(
        [Description("Article ID (GUID).")] Guid id)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var versions = await versionRepo.GetByArticleIdAsync(id);
        var json = JsonSerializer.Serialize(versions.Select(v => new
        {
            id = v.Id,
            versionNumber = v.VersionNumber,
            title = v.Title,
            treePath = v.TreePath,
            createdAt = v.CreatedAt,
            updatedBy = v.UpdatedBy
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_get_article_version")]
    [Description(
        "Get the decrypted content of one specific historical version of an article.\n" +
        "Get valid versionNumber values from bee_get_article_versions first — passing an unknown number " +
        "returns \"Error: version N not found\". Requires an unlocked session; returns an error message " +
        "if locked.\n" +
        "Returns JSON: { id, versionNumber, title, treePath, content, createdAt, updatedBy }.")]
    public async Task<string> GetArticleVersion(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Version number, as returned by bee_get_article_versions. Starts at 1.")] int versionNumber)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var version = await versionRepo.GetAsync(id, versionNumber);
        if (version == null)
            return $"Error: version {versionNumber} not found for article {id}";

        if (!session.IsUnlocked)
            return "Error: session is locked. Unlock first.";

        var masterDek = session.GetMasterDek();
        try
        {
            var isV1 = version.EncryptedDek.Length > 48 && version.EncryptedDek[0] == 0x01;
            var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
            var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
            var articleDek = DekManager.UnwrapDek(version.EncryptedDek, version.DekIV, masterDek, dekAad);
            var content = ArticleEncryptor.Decrypt(version.Ciphertext, version.IV, articleDek, bodyAad);
            Array.Clear(articleDek);

            var json = JsonSerializer.Serialize(new
            {
                id = version.Id,
                versionNumber = version.VersionNumber,
                title = version.Title,
                treePath = version.TreePath,
                content,
                createdAt = version.CreatedAt,
                updatedBy = version.UpdatedBy
            }, JsonOpts);
            return responseManager.ProcessResponse(json);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    [McpServerTool(Name = "bee_get_image")]
    [Description(
        "Get an image from an article. Returns the image as an inline image content block.\n" +
        "Images are automatically resized to fit within the size limit.\n" +
        "Use this to view images referenced in article content (markdown image links like ![](/api/media/{id})).")]
    public async Task<IEnumerable<ContentBlock>> GetImage(
        [Description("Media ID (GUID) — extract from image URLs in article content, e.g. /api/media/{id}")] Guid id,
        [Description("Maximum image size in KB (100-1024). Default: 500. Lower values save context tokens.")] int maxSizeKb = 500)
    {
        if (!session.IsUnlocked)
            return [new TextContentBlock { Text = "Error: session is locked. Unlock first." }];

        var media = await mediaRepo.GetByIdAsync(id);
        if (media == null)
            return [new TextContentBlock { Text = $"Error: media {id} not found" }];

        if (media.ArticleId != null)
        {
            var article = await articleService.GetMetadataAsync(media.ArticleId.Value);
            if (article == null)
                return [new TextContentBlock { Text = "Error: access denied" }];
        }

        byte[] data;
        string contentType;
        string fileName;
        try
        {
            var content = await mediaService.GetContentAsync(id);
            if (content == null)
            {
                return [new TextContentBlock { Text = $"Error: media {id} not found or access denied" }];
            }
            (data, contentType, fileName) = content.Value;
        }
        catch (KeyNotFoundException)
        {
            return [new TextContentBlock { Text = $"Error: media {id} not found" }];
        }

        maxSizeKb = Math.Clamp(maxSizeKb, 100, 1024);
        var maxBytes = maxSizeKb * 1024L;

        if (data.Length <= maxBytes)
        {
            return
            [
                new TextContentBlock { Text = $"Image: {fileName} ({contentType}, {data.Length / 1024}KB)" },
                ToImageBlock(data, contentType)
            ];
        }

        if (contentType == "image/svg+xml")
        {
            return
            [
                new TextContentBlock { Text = $"Image: {fileName} ({contentType}, {data.Length / 1024}KB)" },
                new TextContentBlock { Text = System.Text.Encoding.UTF8.GetString(data) }
            ];
        }

        try
        {
            using var image = Image.Load(data);
            var origWidth = image.Width;
            var origHeight = image.Height;
            var scale = 1.0;
            var quality = 80;

            for (int i = 0; i < 10; i++)
            {
                var newWidth = (int)(origWidth * scale);
                var newHeight = (int)(origHeight * scale);
                var shortestSide = Math.Min(newWidth, newHeight);
                if (shortestSide < 50)
                {
                    var correction = 50.0 / shortestSide;
                    newWidth = (int)(newWidth * correction);
                    newHeight = (int)(newHeight * correction);
                }

                if (i >= 3)
                    quality = Math.Max(quality - 5, 10);

                using var resized = image.Clone(ctx => ctx.Resize(newWidth, newHeight));
                using var ms = new MemoryStream();
                resized.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
                var result = ms.ToArray();

                if (result.Length <= maxBytes)
                {
                    return
                    [
                        new TextContentBlock { Text = $"Image: {fileName} ({contentType}, {data.Length / 1024}KB → {result.Length / 1024}KB)" },
                        ToImageBlock(result, "image/jpeg")
                    ];
                }

                var ratio = (double)maxBytes / result.Length;
                scale = scale * Math.Sqrt(ratio) * 0.85;
            }
        }
        catch
        {
            // ImageSharp couldn't load the image
        }

        return [new TextContentBlock { Text = $"Error: image too large to fit within {maxSizeKb}KB limit" }];
    }

    private static ImageContentBlock ToImageBlock(byte[] imageBytes, string mimeType)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        return new ImageContentBlock
        {
            Data = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(base64)),
            MimeType = mimeType
        };
    }
}
