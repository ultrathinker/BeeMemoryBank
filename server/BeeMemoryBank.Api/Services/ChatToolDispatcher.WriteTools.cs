using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

public sealed partial class ChatToolDispatcher
{
    // ── WRITE tools (Phase 3 — confirm-gated; never run inline by the tool loop) ──
    //
    // Each mirrors the corresponding BeeWriteTools method: same scope-checked ArticleService calls,
    // same ACL-error handling (ReadOnlyAccessException → "read-only folder", UnauthorizedAccessException
    // → "restricted folder"), same two-step confirm for delete. ACL is enforced BY REUSE — the Core
    // repos throw these on write; we surface them as graceful tool results, never exceptions. A
    // locked vault is already rejected up-front in InvokeAsync for every call that actually needs
    // the master DEK (see RequiresUnlockedSessionForCall) — a metadata-only bee_update_article and
    // any bee_delete_article reach here even while locked, exactly like their MCP counterparts.

    // Mirrors BeeWriteTools.SaveArticle / ArticleEndpoints POST /api/articles.
    private async Task<string> SaveArticleAsync(JsonElement args)
    {
        var title = args.TryGetProperty("title", out var t) ? t.GetString() : null;
        var treePath = args.TryGetProperty("treePath", out var tp) ? tp.GetString() : null;
        var content = args.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return ErrorJson("title is required");
        if (string.IsNullOrWhiteSpace(treePath)) return ErrorJson("treePath is required");
        if (content == null) return ErrorJson("content is required");
        var tags = args.TryGetProperty("tags", out var tagsEl) ? ReadTags(tagsEl) : [];

        try
        {
            // CreateAsync encrypts the body under a per-article DEK (wrapped under the master DEK),
            // creates folders as needed, and logs the create event (synced like a human edit).
            //
            // Tags go straight into CreateAsync, which writes the article, its body, its tags and
            // the CREATE event inside ONE transaction — so the event carries the DB-canonical tag
            // set and there is no moment where the article exists untagged.
            //
            // This briefly used the two-step shape (create with no tags, then SetForArticleAsync)
            // to match BeeWriteTools.SaveArticle and ArticleEndpoints POST /api/articles, whose
            // CREATE event carries an empty tag set with a separate tag-set event after. That
            // traded a real failure mode for cosmetic event parity: if the second step throws, the
            // article is already committed untagged while the caller is told the save failed, and
            // a retry then duplicates or collides. MCP and REST should move TO this atomic form —
            // the divergence is theirs to fix, not a reason to copy it here.
            var article = await articleService.CreateAsync(title!, treePath!, tags, content);
            return OkJson($"Created article '{article.Title}' in {article.TreePath}.", article.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: target folder is restricted.");
        }
        catch (ArgumentException ex) { return ErrorJson(ex.Message); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.UpdateArticle / ArticleEndpoints PUT /api/articles/{id}. Only provided
    // fields are touched (ArticleService.UpdateAsync keeps omitted fields). Content is optional.
    private async Task<string> UpdateArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");

        string? title = args.TryGetProperty("title", out var t2) ? t2.GetString() : null;
        string? treePath = args.TryGetProperty("treePath", out var tp2) ? tp2.GetString() : null;
        string? content = args.TryGetProperty("content", out var c2) ? c2.GetString() : null;
        // tags: absent → null (keep current); present (even []) → replace (matches BeeWriteTools).
        List<string>? tags = args.TryGetProperty("tags", out var tagsEl) ? ReadTags(tagsEl) : null;
        var hasContent = content != null;

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");
        // Protected (second-layer) bodies are opaque to agents — a human must edit them in the UI.
        if (article.Protected && hasContent)
            return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body. A human must edit it in the web/mobile UI.");

        try
        {
            // Tags go into UpdateAsync itself — one transaction, one event carrying the final tag
            // set. See the comment in SaveArticleAsync for why the two-step shape was reverted.
            await articleService.UpdateAsync(id, title, treePath, tags, content);
            return OkJson($"Updated article {id} ({article.Title}).", id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
        catch (ArgumentException ex) { return ErrorJson(ex.Message); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.AppendToArticle.
    private async Task<string> AppendToArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");
        var text = args.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        if (string.IsNullOrEmpty(text)) return ErrorJson("text is required");

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");
        if (article.Protected)
            return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body.");

        try
        {
            // Same per-article lock as the MCP tool — chat writes race agent writes just as easily.
            var newLength = await articleService.AppendAsync(id, text);
            return OkJson($"Appended to article {id} ({article.Title}). New size: {newLength} chars.", id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.ReplaceInArticle (case-sensitive substring replace; N=0 is a no-op).
    private async Task<string> ReplaceInArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");
        var search = args.TryGetProperty("search", out var se) ? se.GetString() : null;
        var replace = args.TryGetProperty("replace", out var re) ? re.GetString() : null;
        if (string.IsNullOrEmpty(search)) return ErrorJson("search is required");
        if (replace == null) return ErrorJson("replace is required");
        if (search == replace) return ErrorJson("search and replace are identical");

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");
        if (article.Protected)
            return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body.");

        try
        {
            var count = await articleService.ReplaceInAsync(id, search, replace);
            if (count == 0)
                return JsonSerializer.Serialize(new { ok = true, occurrences = 0, message = $"Replaced 0 occurrences of \"{Truncate(search, 50)}\" in article {id} ({article.Title}).", id }, JsonOpts);
            return JsonSerializer.Serialize(new { ok = true, occurrences = count, message = $"Replaced {count} occurrence(s) of \"{Truncate(search, 50)}\" → \"{Truncate(replace, 50)}\" in article {id} ({article.Title}).", id }, JsonOpts);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.DeleteArticle. Keeps the two-step `confirm` flag semantics: the method
    // itself warns when confirm is false and only soft-deletes when true. In the chat flow the
    // confirm endpoint forces confirm=true at execution (the human's Allow click IS the confirmation),
    // so the two-step check stays intact as defense-in-depth without double-prompting the user.
    private async Task<string> DeleteArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");
        var confirm = args.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True;
        if (!confirm)
            return ErrorJson($"Warning: this will soft-delete article {id}. Set confirm=true to proceed. (In chat the user is asked to approve first.)");

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");

        try
        {
            await articleService.DeleteAsync(id);
            return OkJson($"Deleted article {id} ({article.Title}). It is hidden from search/lists and can be restored from the web UI.", id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
    }

    // Inserts a chat attachment image into an article: uploads the blob into the article media store
    // (MediaService — same path as POST /api/media) and appends/creates the markdown reference.
    // Attachment ownership is enforced by the user-scoped repo lookup; article/media write ACL is
    // enforced at the repository layer (MediaRepository.EnsureWriteAllowedAsync + ArticleRepository
    // scope checks throw UnauthorizedAccessException / ReadOnlyAccessException), which is caught here
    // and surfaced as a graceful tool result.
    private async Task<string> InsertImageIntoArticleAsync(JsonElement args, HttpContext ctx)
    {
        if (!args.TryGetProperty("attachmentId", out var attEl) || !Guid.TryParse(attEl.GetString(), out var attachmentId))
            return ErrorJson("attachmentId (GUID) is required");

        Guid? articleId = args.TryGetProperty("articleId", out var aEl) && Guid.TryParse(aEl.GetString(), out var aid) ? aid : null;
        var title = args.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
        var treePath = args.TryGetProperty("treePath", out var tpEl) ? tpEl.GetString() : null;
        var caption = args.TryGetProperty("caption", out var cEl) ? cEl.GetString() : null;

        var newArticle = articleId is null;
        if (newArticle && (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(treePath)))
            return ErrorJson("Provide either articleId (existing article) or title + treePath (new article).");
        if (!newArticle && (title != null || treePath != null))
            return ErrorJson("Provide articleId OR title+treePath, not both.");

        // Ownership: the attachment must belong to a conversation of the calling user.
        var (userId, _, _) = CallerIdentity.Extract(ctx);
        if (userId is null) return ErrorJson("Unauthorized");
        var attachment = await attachRepo.GetByIdForUserAsync(attachmentId, userId.Value, session);
        if (attachment?.Blob is not { Length: > 0 })
            return ErrorJson($"attachment {attachmentId} not found (use an attachmentId from the attachment manifest or a generate_image result, copied exactly)");

        try
        {
            var fileName = "chat-image-" + attachmentId.ToString("N") + ExtensionForMime(attachment.Mime);
            var alt = string.IsNullOrWhiteSpace(caption) ? "image" : caption!.Trim();

            if (newArticle)
            {
                // New-article orphan-avoidance: upload the image as an ORPHAN media row FIRST
                // (articleId: null — MediaRepository skips the article ACL/scope check when there is
                // no article yet), THEN create the article with the image markdown already embedded in
                // its initial content. ArticleService.CreateAsync calls LinkOrphanMediaAsync, which
                // binds the orphan media referenced in the body to the newly created article. On any
                // failure between the two steps only an invisible orphan media blob remains — never a
                // visible empty article.
                var media = await mediaService.CreateAsync(fileName, attachment.Mime, attachment.Blob!, null);
                var figureMd = $"![{alt}](/api/media/{media.Id})";
                var created = await articleService.CreateAsync(title!, treePath!, [], figureMd);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = $"Created article '{created.Title}' in {created.TreePath} with the inserted image ({figureMd}).",
                    id = created.Id,          // "id" so TryExtractArticleId gives the UI an open-article link
                    mediaId = media.Id,
                    mediaUrl = $"/api/media/{media.Id}"
                }, JsonOpts);
            }

            // Existing-article branch: resolve the target, reject protected bodies, then upload media
            // bound to the resolved article and append the figure markdown.
            var article = await articleService.GetMetadataAsync(articleId!.Value);
            if (article == null) return ErrorJson($"article {articleId} not found");
            if (article.Protected)
                return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body.");

            var existingMedia = await mediaService.CreateAsync(fileName, attachment.Mime, attachment.Blob!, article.Id);
            var existingFigureMd = $"![{alt}](/api/media/{existingMedia.Id})";
            try
            {
                // AppendAsync always inserts the blank-line separator; an empty body would gain a
                // leading one, so that case still goes through UpdateAsync with the exact text.
                var existing = await articleService.GetContentAsync(article.Id);
                if (string.IsNullOrEmpty(existing))
                    await articleService.UpdateAsync(article.Id, null, null, null, existingFigureMd);
                else
                    await articleService.AppendAsync(article.Id, existingFigureMd);
            }
            catch
            {
                // Compensate: don't leave an orphan media blob if the body update failed.
                try { await mediaService.DeleteAsync(existingMedia.Id); } catch { /* best effort */ }
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                message = $"Inserted image into article {article.Id} ({article.Title}) as {existingFigureMd}.",
                id = article.Id,          // "id" so TryExtractArticleId gives the UI an open-article link
                mediaId = existingMedia.Id,
                mediaUrl = $"/api/media/{existingMedia.Id}"
            }, JsonOpts);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: target folder is restricted.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {articleId} not found"); }
        catch (ArgumentException ex) { return ErrorJson(ex.Message); }        // MediaService size/type limits
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }

        static string ExtensionForMime(string mime) => mime.ToLowerInvariant() switch
        {
            "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp",
            "image/gif" => ".gif", "image/svg+xml" => ".svg", _ => ".img"
        };
    }
}
