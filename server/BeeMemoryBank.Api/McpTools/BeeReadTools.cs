using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.AspNetCore.Http;
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
    SessionService session,
    McpResponseManager responseManager,
    MediaService mediaService,
    IMediaRepository mediaRepo,
    IConceptTagRepository conceptTagRepo,
    ArticleDiffService articleDiffService,
    TreeService treeService,
    FolderAccessService folderAccess,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Shared by bee_list_articles' updatedAfter and bee_get_article_diff's baselineAt. RoundtripKind
    // preserves whatever Kind the input string implies (Z-suffixed -> Utc, bare -> Unspecified) so the
    // resulting DateTime re-serializes via the global Dapper DateTimeTypeHandler ("o" format) back into
    // byte-identical text against tbl_article.updated_at, which itself is a mix of Utc (app writes) and
    // Unspecified (imported rows without a zone marker) strings. An explicit-offset input parses as Local
    // and must be normalized to Utc or it would compare against the server machine's local timezone.
    private static DateTime ParseTimestamp(string input)
    {
        var parsed = DateTime.Parse(input, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed.Kind == DateTimeKind.Local) parsed = parsed.ToUniversalTime();
        return parsed;
    }

    [McpServerTool(Name = "bee_list_articles")]
    [Description(
        "List articles, optionally filtered by tree path. Soft-deleted articles are not included.\n" +
        "Returns JSON array: [{ id, title, treePath, status, createdAt, updatedAt }]. " +
        "The treePath filter matches articles whose TreePath equals (or is a descendant of) the given path. " +
        "Omit treePath to list everything. For a tree-structured view with empty folders, use bee_get_tree. " +
        "Optional updatedAfter (ISO-8601) restricts results to articles whose updatedAt is strictly greater — " +
        "pass the max updatedAt from your previous call to get just the delta.")]
    public async Task<string> ListArticles(
        [Description("Tree path filter, e.g. '/Work' or '/Work/Dev'. Omit to list all articles.")] string? treePath = null,
        [Description("ISO-8601 timestamp. Return only articles whose updatedAt is strictly greater (>) than this. Pass the max updatedAt from your previous bee_list_articles call to get just the delta since then.")] string? updatedAfter = null)
    {
        DateTime? parsedUpdatedAfter = null;
        if (updatedAfter != null)
        {
            try
            {
                parsedUpdatedAfter = ParseTimestamp(updatedAfter);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return $"Error: invalid updatedAfter timestamp: {ex.Message}";
            }
        }

        var articles = await articleService.ListAsync(treePath, parsedUpdatedAfter);

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
        "Get an article (full body by default). Pass content=false for metadata only.\n" +
        "Returns JSON: { id, title, treePath, tags, relatedCount, relatedStrength, createdAt, updatedAt" +
        "[, content] }. 'tags' is a string array of tag names on the article. 'relatedCount' = how many " +
        "other articles share at least one tag with this one; 'relatedStrength' = total sum of shared-tag " +
        "counts across all related articles.\n" +
        "Soft-deleted articles return \"Error: article {id} was deleted\" (distinct from " +
        "\"not found\" for a nonexistent id), for callers with access to the article's folder.")]
    public async Task<string> GetArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Include the decrypted article body as 'content' in the response. Default: true. Pass false for metadata only.")] bool content = true)
    {
        // No ambient HttpContext means this ran outside a real HTTP request (e.g. a test harness
        // that constructs BeeReadTools directly) -- the primary access gate (GetMetadataAsync's
        // CallerScope filter, below) already applies regardless, so fail OPEN on just this extra
        // defense-in-depth re-check rather than deny every caller we have no way to identify.
        // Every real MCP request has a live HttpContext here.
        var httpCtx = httpContextAccessor.HttpContext;
        int? userId; int? agentId; bool isSuperadmin;
        if (httpCtx != null)
            (userId, agentId, isSuperadmin) = BeeMemoryBank.Api.Helpers.CallerIdentity.Extract(httpCtx);
        else
            (userId, agentId, isSuperadmin) = (null, null, true);

        var gate = await BeeMemoryBank.Api.Helpers.ArticleContentPolicy.ResolveAsync(
            id, content, userId, agentId, isSuperadmin, articleService, session, folderAccess);

        if (gate.Status == BeeMemoryBank.Api.Helpers.ArticleContentPolicy.Status.NotFound)
        {
            // Distinguish "soft-deleted" from "never existed" for callers with access to the
            // article's folder; GetMetadataAsync(includeDeleted:true) still enforces folder-scope
            // access inside ArticleRepository.GetByIdAsync, so this cannot leak existence across
            // an access boundary -- a caller without access sees "not found" either way.
            var deleted = await articleService.GetMetadataAsync(id, includeDeleted: true);
            if (deleted != null && deleted.Status != "A")
                return $"Error: article {id} was deleted (deletedAt: {deleted.DeletedAt:o}).";
            return $"Error: article {id} not found";
        }

        var article = gate.Article!;
        var tags = await conceptTagRepo.GetByArticleIdAsync(id);
        var related = await conceptTagRepo.GetRelatedArticlesAsync(id);
        var relatedCount = related.Count;
        var relatedStrength = related.Sum(r => r.Strength);

        switch (gate.Status)
        {
            case BeeMemoryBank.Api.Helpers.ArticleContentPolicy.Status.Protected:
                // Protected articles are passphrase-locked end-to-end. An agent has no passphrase
                // and must never receive (or accidentally rewrite) the BMBENC1 ciphertext.
                return responseManager.ProcessResponse(JsonSerializer.Serialize(new
                {
                    id = article.Id,
                    title = article.Title,
                    treePath = article.TreePath,
                    tags,
                    relatedCount,
                    relatedStrength,
                    content = (string?)null,
                    isProtected = true,
                    notice = "This article is password-protected (second-layer encryption). Its body can only be unlocked by a human in the web/mobile UI; agents cannot read or modify it.",
                    createdAt = article.CreatedAt,
                    updatedAt = article.UpdatedAt
                }, JsonOpts));

            case BeeMemoryBank.Api.Helpers.ArticleContentPolicy.Status.Locked:
                return responseManager.ProcessResponse(JsonSerializer.Serialize(new
                {
                    id = article.Id,
                    title = article.Title,
                    treePath = article.TreePath,
                    tags,
                    relatedCount,
                    relatedStrength,
                    content = (string?)null,
                    isLocked = true,
                    notice = "The vault is locked. Unlock it to read article content; metadata is still available."
                }, JsonOpts));

            case BeeMemoryBank.Api.Helpers.ArticleContentPolicy.Status.AccessDenied:
                return responseManager.ProcessResponse(JsonSerializer.Serialize(new
                {
                    id = article.Id,
                    title = article.Title,
                    treePath = article.TreePath,
                    tags,
                    relatedCount,
                    relatedStrength,
                    content = (string?)null,
                    accessDenied = true,
                    notice = "You don't have permission to read this article's content."
                }, JsonOpts));

            case BeeMemoryBank.Api.Helpers.ArticleContentPolicy.Status.Ok when content:
                return responseManager.ProcessResponse(JsonSerializer.Serialize(new
                {
                    id = article.Id,
                    title = article.Title,
                    treePath = article.TreePath,
                    tags,
                    relatedCount,
                    relatedStrength,
                    content = gate.Content,
                    createdAt = article.CreatedAt,
                    updatedAt = article.UpdatedAt
                }, JsonOpts));

            default: // Ok, metadata only (content=false)
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
    }

    [McpServerTool(Name = "bee_get_tree")]
    [Description(
        "Get the folder/article tree. Unlike bee_list_articles, this includes empty folders too.\n" +
        "Returns JSON: { paths: [{ path, isSystem, isRemote, articles: [{ id, title }] }] }, sorted " +
        "alphabetically by path. Each entry represents one folder and its direct articles (no body/tags " +
        "— fetch with bee_get_article if needed). Soft-deleted folders/articles are excluded.\n" +
        "Use 'path' to scope the view to one subtree (e.g. '/Work').\n" +
        "SCALE: against a large vault this can return tens of thousands of entries. If this call's " +
        "response looks truncated, or you only need part of the tree, narrow the call rather than " +
        "re-fetching everything: pass 'path' to scope to a subtree, pass 'depth' to limit how many " +
        "levels below 'path' are returned, and/or pass 'limit' + 'offset' to page through the entries. " +
        "When 'depth' or 'limit' is supplied, the response also includes pagination metadata " +
        "({ depth, limit, offset, total, truncated }) so you can tell whether more entries remain " +
        "(more remain while offset + paths.length < total, or while truncated=true).\n" +
        "Omitting depth/limit/offset reproduces the legacy unbounded behavior exactly (the response " +
        "is then just { paths: [...] } with no extra keys). Results are already scoped to the caller's " +
        "accessible folders; depth/limit never expose anything outside that scope.")]
    public async Task<string> GetTree(
        [Description("Path filter, e.g. '/Work'. Shows only that folder and its descendants. Omit for the whole tree.")] string? path = null,
        [Description("Max number of path levels to descend below 'path' (or below root if 'path' is omitted). null = unlimited (legacy behavior). 'path' itself is level 0, its direct children are level 1, etc.")] int? depth = null,
        [Description("Max number of folder+article entries to return in this call, after depth filtering. null = no pagination (return all matching entries). Use with 'offset' to page through large subtrees.")] int? limit = null,
        [Description("Number of matching entries to skip before the returned page (for pagination). Only meaningful together with 'limit'. Default 0.")] int offset = 0)
    {
        var result = await treeService.GetTreePathsAsync(path, depth, limit, offset);

        // Build the paths payload with the EXACT same anonymous shape the legacy inline build used,
        // so omitting depth/limit/offset stays byte-for-byte identical to the pre-WP-19 response.
        var pathEntries = result.Paths.Select(p => new
        {
            path = p.Path,
            isSystem = p.IsSystem,
            isRemote = p.IsRemote,
            articles = p.Articles.Select(a => (object)new { id = a.Id, title = a.Title }).ToList()
        });

        // Only attach pagination metadata when the caller actually used a limiting parameter —
        // the no-args call must keep returning exactly { "paths": [...] }.
        string json;
        if (depth.HasValue || limit.HasValue || offset != 0)
        {
            json = JsonSerializer.Serialize(new
            {
                paths = pathEntries,
                depth = result.Depth,
                limit = result.Limit,
                offset = result.Offset,
                total = result.Total,
                truncated = result.Truncated
            }, JsonOpts);
        }
        else
        {
            json = JsonSerializer.Serialize(new { paths = pathEntries }, JsonOpts);
        }
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
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
    public async Task<string> GetArticleVersion(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Version number, as returned by bee_get_article_versions. Starts at 1.")] int versionNumber)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";
        if (article.Protected)
            return "Error: this article is password-protected (second-layer encryption); version content is locked and cannot be read by agents.";

        var version = await versionRepo.GetAsync(id, versionNumber);
        if (version == null)
            return $"Error: version {versionNumber} not found for article {id}";

        if (!session.IsUnlocked)
            return "Error: session is locked. Unlock first.";

        var masterDek = session.GetMasterDek();
        try
        {
            var content = DecryptVersionContent(version, masterDek);

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

    private static string DecryptVersionContent(BeeMemoryBank.Core.Models.ArticleVersion version, byte[] masterDek)
    {
        var isV1 = version.EncryptedDek.Length > 48 && version.EncryptedDek[0] == 0x01;
        var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(version.ArticleId.ToByteArray()).ToArray() : null;
        var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(version.ArticleId.ToByteArray()).ToArray() : null;
        var articleDek = DekManager.UnwrapDek(version.EncryptedDek, version.DekIV, masterDek, dekAad);
        try
        {
            return ArticleEncryptor.Decrypt(version.Ciphertext, version.IV, articleDek, bodyAad);
        }
        finally
        {
            Array.Clear(articleDek);
        }
    }

    [McpServerTool(Name = "bee_get_article_diff")]
    [Description(
        "Return what changed in the article since a point in time. baselineAt is compared against the version " +
        "history (snapshot-before-write: the baseline is the earliest version created after baselineAt). Returns " +
        "markdown-block-level changes ready for bee_replace_in_article, plus a similarity score; unchanged=true " +
        "when nothing changed after baselineAt.")]
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
    public async Task<string> GetArticleDiff(
        [Description("Article ID (GUID).")] Guid id,
        [Description("ISO-8601 timestamp. The comparison baseline is the article's state as of this moment (see the earliest-version-after-this-time rule in the tool description).")] string baselineAt)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";
        if (article.Protected)
            return "Error: this article is password-protected (second-layer encryption); version content is locked and cannot be read by agents.";
        if (!session.IsUnlocked)
            return "Error: session is locked. Unlock first.";

        DateTime baselineAtParsed;
        try
        {
            baselineAtParsed = ParseTimestamp(baselineAt);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return $"Error: invalid baselineAt timestamp: {ex.Message}";
        }

        string BuildResponse(object? baseline, bool unchanged, double similarity, bool tooLarge, IEnumerable<object> blocks) =>
            responseManager.ProcessResponse(JsonSerializer.Serialize(new
            {
                id,
                baseline,
                current = new { updatedAt = article.UpdatedAt },
                unchanged,
                similarity,
                tooLarge,
                blocks
            }, JsonOpts));

        var baselineVersion = await versionRepo.GetEarliestAfterAsync(id, baselineAtParsed);
        if (baselineVersion == null)
        {
            return article.UpdatedAt <= baselineAtParsed
                ? BuildResponse(null, unchanged: true, similarity: 1.0, tooLarge: false, blocks: [])
                : BuildResponse(null, unchanged: false, similarity: 0.0, tooLarge: false, blocks: []);
        }

        string baselineContent;
        var masterDek = session.GetMasterDek();
        try
        {
            baselineContent = DecryptVersionContent(baselineVersion, masterDek);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
        finally
        {
            Array.Clear(masterDek);
        }

        string currentContent;
        try
        {
            currentContent = await articleService.GetContentAsync(id);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }

        var diffResult = articleDiffService.Diff(baselineContent, currentContent);

        return BuildResponse(
            baseline: new { versionNumber = baselineVersion.VersionNumber, createdAt = baselineVersion.CreatedAt },
            unchanged: diffResult.Unchanged,
            similarity: diffResult.Similarity,
            tooLarge: diffResult.TooLarge,
            blocks: diffResult.Blocks.Select(b => new { op = b.Op, heading = b.Heading, old = b.Old, @new = b.New }));
    }

    [McpServerTool(Name = "bee_get_image")]
    [Description(
        "Get an image from an article. Returns the image as an inline image content block.\n" +
        "Images are automatically resized to fit within the size limit.\n" +
        "Use this to view images referenced in article content (markdown image links like ![](/api/media/{id})).")]
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
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
