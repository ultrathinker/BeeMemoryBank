using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

public partial class ArticleService(
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    SessionService session,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    IEventLogger eventLogger,
    IMediaRepository mediaRepo,
    IFolderRepository folderRepo,
    IArticleVersionRepository versionRepo,
    IActorProvider actorProvider,
    ConceptTagService conceptTagService,
    IDbConnectionFactory connFactory)
{
    [GeneratedRegex(@"!\[[^\]]*\]\(/api/media/([0-9a-fA-F-]{36})\)")]
    private static partial Regex MediaRefRegex();

    /// <summary>
    /// Creates an article with encrypted body.
    /// Generates per-article DEK, encrypts body, saves both layers, writes to event log.
    /// </summary>
    public async Task<Article> CreateAsync(string title, string treePath, List<string> tags, string plaintext, string? protectionHint = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title cannot be empty.");
        if (string.IsNullOrWhiteSpace(treePath) || !treePath.StartsWith('/'))
            throw new ArgumentException("Path must start with '/'.");
        // Reject "..", ".", "//", control chars before the path is persisted —
        // see TreePathCanonicalizer for rationale.
        treePath = TreePathCanonicalizer.Canonicalize(treePath);

        var masterDek = session.GetMasterDek();
        Guid articleId = Guid.NewGuid();
        byte[] ciphertext, iv, encryptedDek, dekIv;
        try
        {
            var articleDek = DekManager.GenerateArticleDek();
            try
            {
                var dekAad = "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();
                var bodyAad = "bmb-art-body"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();
                (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek, bodyAad);
                (encryptedDek, dekIv) = DekManager.WrapDek(articleDek, masterDek, dekAad);
            }
            finally
            {
                Array.Clear(articleDek);
            }
        }
        finally
        {
            Array.Clear(masterDek);
        }

        var lamportTs = clock.Tick();
        var identity = await nodeRepo.GetAsync();
        var now = DateTime.UtcNow;

        var article = new Article
        {
            Id = articleId,
            Title = title,
            TreePath = treePath,
            Status = "A",
            LamportTs = lamportTs,
            SourceNodeId = identity?.NodeId,
            CreatedAt = now,
            UpdatedAt = now,
            // Derive the protected flag from the body itself so a copied/imported BMBENC1 blob
            // (e.g. via CopyService) is never silently treated as plaintext.
            Protected = ProtectedContentCodec.IsProtected(plaintext),
            // Only carry a hint when the body is actually protected — a hint on a plaintext article
            // would be a confusing, meaningless dangling field.
            ProtectionHint = ProtectedContentCodec.IsProtected(plaintext) ? protectionHint : null
        };

        // Note: Folder auto-vivification stays outside the transaction (an ancestor folder
        // vivified by a write that later rolls back is an inert, harmless empty folder).
        var folder = await EnsureFolderExistsAsync(treePath);
        article.FolderId = folder?.Id;

        var body = new EncryptedArticleBody
        {
            ArticleId = article.Id,
            Ciphertext = ciphertext,
            IV = iv,
            EncryptedDek = encryptedDek,
            DekIV = dekIv
        };

        // Precompute new-tag embeddings in memory before starting the SQLite transaction
        var precomputedEmbeddings = tags.Count > 0
            ? await conceptTagService.PrecomputeNewTagEmbeddingsAsync(tags)
            : null;

        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await articleRepo.CreateAsync(article, tx);
                await bodyRepo.UpsertAsync(body, tx);

                if (tags.Count > 0)
                {
                    await conceptTagService.SetForArticleAsync(article.Id, tags, precomputedEmbeddings, tx);
                }

                // Read tags back through the SAME transaction so the sync event carries the
                // DB-canonical name/casing, not whatever casing the caller happened to pass.
                var conceptTags = (await conceptTagService.GetByArticleIdAsync(article.Id, tx)).ToArray();

                await eventLogger.LogCreateAsync(article, body, conceptTags, tx);

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back; don't mask the real failure */ }
                throw;
            }
        }

        // No embedding-vector-cache invalidation here on purpose: this freshly-created Article's
        // EmbeddingProjection is never set anywhere above (embeddings are generated asynchronously,
        // later, by PendingEmbeddingProcessor), so the row lands in tbl_article with a NULL
        // projection and the cache's rebuild query (`WHERE embedding_projection IS NOT NULL`) would
        // never have picked it up anyway. The path that actually gives this article a projection --
        // EmbeddingProjectionService.ProjectArticleAsync, via ArticleRepository.UpdateEmbeddingUnscopedAsync
        // -- already invalidates (incrementally) the moment it happens. Invalidating here too used to
        // force a full corpus-wide cache rebuild (~150MB of SQLite reads at 100k articles) on every
        // single create, for a row the rebuild wouldn't even include. Do not "restore" this call --
        // see EmbeddingVectorCache's own doc comment for the full reasoning.
        eventLogger.SignalSync();

        await LinkOrphanMediaAsync(article.Id, plaintext);

        return article;
    }

    /// <summary>Returns metadata without decrypting the body. Pass includeDeleted:true to also
    /// return soft-deleted articles (still subject to folder-scope access checks).</summary>
    public Task<Article?> GetMetadataAsync(Guid id, bool includeDeleted = false) => articleRepo.GetByIdAsync(id, includeDeleted);

    /// <summary>Decrypts and returns the article body. Requires an unlocked session.</summary>
    public async Task<string> GetContentAsync(Guid id)
    {
        var body = await bodyRepo.GetByArticleIdAsync(id);
        if (body == null) throw new KeyNotFoundException($"Article body {id} not found.");

        var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
        var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;

        var articleDek = session.TryUnwrapWithCandidates(masterDek =>
            DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, dekAad));
        try
        {
            var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
            return ArticleEncryptor.Decrypt(body.Ciphertext, body.IV, articleDek, bodyAad);
        }
        finally
        {
            Array.Clear(articleDek);
        }
    }

    /// <summary>
    /// Updates article metadata and/or body.
    /// If plaintext == null — body is not touched (DEK is preserved).
    /// If plaintext is provided — re-encrypted with the same DEK but a new IV.
    /// </summary>
    public async Task UpdateAsync(
        Guid id,
        string? title = null,
        string? treePath = null,
        List<string>? tags = null,
        string? plaintext = null,
        string? protectionHint = null,
        bool updateHint = false,
        bool suppressVersion = false)
    {
        using var _ = await ArticleWriteLock.AcquireAsync(id);
        await UpdateCoreAsync(id, title, treePath, tags, plaintext, protectionHint, updateHint, suppressVersion);
    }

    /// <summary>
    /// The update itself, assuming the caller already holds this article's write lock. Exists so
    /// the read-modify-write operations below can hold ONE lock across both the read and the
    /// write — <see cref="ArticleWriteLock"/> is not reentrant, so they cannot call the public
    /// <see cref="UpdateAsync"/> from inside their own critical section.
    /// </summary>
    private async Task UpdateCoreAsync(
        Guid id,
        string? title,
        string? treePath,
        List<string>? tags,
        string? plaintext,
        string? protectionHint,
        bool updateHint,
        bool suppressVersion,
        int? purgeHistoryKeepCount = null)
    {
        var article = await articleRepo.GetByIdAsync(id)
                       ?? throw new KeyNotFoundException($"Article {id} not found.");

        var prevTitle = article.Title;
        var prevTreePath = article.TreePath;

        if (title != null) article.Title = title;
        if (treePath != null)
        {
            treePath = TreePathCanonicalizer.Canonicalize(treePath);
            // Note: Folder auto-vivification stays outside the transaction (Correction 4)
            var folder = await EnsureFolderExistsAsync(treePath);
            article.FolderId = folder?.Id;
            article.TreePath = treePath;
        }

        var identity = await nodeRepo.GetAsync();
        article.LamportTs = clock.Tick();
        article.SourceNodeId = identity?.NodeId;
        // Single clock read shared with the version snapshot below (if any) - bee_get_article_diff's
        // baseline rule ("earliest version with CreatedAt > baselineAt") depends on version.CreatedAt
        // never being later than the article.UpdatedAt from the SAME write. Two separate
        // DateTime.UtcNow calls straddling the DB round-trips in between (folder lookup, article
        // update, tag set, body fetch) reliably drift by ~1ms, which made a diff called with
        // baselineAt == that exact updatedAt see the version as "created after" and re-report the
        // edit that produced it as pending.
        var now = DateTime.UtcNow;
        article.UpdatedAt = now;

        // Keep the protected flag in lock-step with the body content (the body is the source of
        // truth). Only touch it when the body is actually being rewritten.
        if (plaintext != null)
        {
            article.Protected = ProtectedContentCodec.IsProtected(plaintext);

            // Re-flag both derived-search-artifact pending flags together whenever content
            // actually changes -- EmbeddingPending/IndexPending both mean "stale, needs
            // reprocessing," they just drive two independent background processors
            // (PendingEmbeddingProcessor / PendingIndexProcessor). Note: prior to WP-11, nothing
            // in this method re-set EmbeddingPending on an edit either (only the Article model's
            // constructor default covered brand-new articles) -- a pre-existing gap that meant an
            // edited article's embedding silently went stale after its first successful
            // generation. Fixed here alongside adding IndexPending, since the correct shared
            // behavior for "content changed" is the same for both flags.
            article.EmbeddingPending = true;
            article.IndexPending = true;
        }
        if (updateHint)
            article.ProtectionHint = protectionHint;

        // Precompute new tag embeddings in memory before starting the SQLite transaction (Correction 1)
        var precomputedTagEmbeddings = tags != null
            ? await conceptTagService.PrecomputeNewTagEmbeddingsAsync(tags)
            : null;

        EncryptedArticleBody? body = null;
        ArticleVersion? versionToCreate = null;

        if (plaintext != null)
        {
            var existingBody = await bodyRepo.GetByArticleIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article body {id} not found.");

            // suppressVersion: skip snapshotting the CURRENT body. Used by Protect/ChangePassphrase,
            // where the current body is pre-protection plaintext (or old-passphrase ciphertext) that
            // must NOT linger in history. Those callers purge history via purgeHistoryKeepCount below,
            // in the SAME transaction as the protected body write — either both land or neither does,
            // so there is never a window where a protected article has a readable plaintext version.
            if (!suppressVersion)
            {
                // Read outside the transaction — safe because ArticleWriteLock already serializes
                // every writer to this article, in-process, for the whole call, and versionRepo has
                // exactly one caller (this method) that ever creates a version row, so no other
                // writer can race this read against the eventual INSERT below.
                var maxVer = await versionRepo.GetMaxVersionNumberAsync(id);
                var actorName = actorProvider.ActorName ?? actorProvider.ActorType;
                var nodeDisplayName = identity?.DisplayName;
                var updatedBy = nodeDisplayName != null
                    ? $"{nodeDisplayName} / {actorName}"
                    : actorName;
                versionToCreate = new ArticleVersion
                {
                    Id = Guid.NewGuid(),
                    ArticleId = id,
                    VersionNumber = maxVer + 1,
                    Title = prevTitle,
                    TreePath = prevTreePath,
                    Ciphertext = existingBody.Ciphertext,
                    IV = existingBody.IV,
                    EncryptedDek = existingBody.EncryptedDek,
                    DekIV = existingBody.DekIV,
                    UpdatedBy = updatedBy,
                    CreatedAt = now
                };
            }

            var masterDek = session.GetMasterDek();
            try
            {
                var isV1 = existingBody.EncryptedDek.Length > 48 && existingBody.EncryptedDek[0] == 0x01;
                var unwrapAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
                var articleDek = DekManager.UnwrapDek(existingBody.EncryptedDek, existingBody.DekIV, masterDek, unwrapAad);
                try
                {
                    var dekAad = "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray();
                    var bodyAad = "bmb-art-body"u8.ToArray().Concat(id.ToByteArray()).ToArray();
                    var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek, bodyAad);
                    var (encryptedDek, dekIv) = DekManager.WrapDek(articleDek, masterDek, dekAad);
                    body = new EncryptedArticleBody
                    {
                        ArticleId = id,
                        Ciphertext = ciphertext,
                        IV = iv,
                        EncryptedDek = encryptedDek,
                        DekIV = dekIv
                    };
                }
                finally
                {
                    Array.Clear(articleDek);
                }
            }
            finally
            {
                Array.Clear(masterDek);
            }
        }
        else
        {
            body = await bodyRepo.GetByArticleIdAsync(id);
        }

        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                if (purgeHistoryKeepCount.HasValue)
                {
                    await versionRepo.DeleteOldVersionsAsync(id, purgeHistoryKeepCount.Value, tx);
                }

                await articleRepo.UpdateAsync(article, tx);

                if (tags != null)
                {
                    await conceptTagService.SetForArticleAsync(id, tags, precomputedTagEmbeddings, tx);
                }

                if (versionToCreate != null)
                {
                    await versionRepo.CreateAsync(versionToCreate, tx);
                    await versionRepo.DeleteOldVersionsAsync(id, 50, tx);
                }

                if (plaintext != null && body != null)
                {
                    await bodyRepo.UpsertAsync(body, tx);
                }

                // Read tags back through the SAME transaction so the sync event carries the
                // DB-canonical name/casing, not whatever casing the caller happened to pass.
                var conceptTags = (await conceptTagService.GetByArticleIdAsync(id, tx)).ToArray();

                await eventLogger.LogUpdateAsync(article, body, conceptTags, tx);

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back; don't mask the real failure */ }
                throw;
            }
        }

        // No embedding-vector-cache invalidation here on purpose. This method never sets
        // `article.EmbeddingProjection` itself -- `article` came from articleRepo.GetByIdAsync above
        // and only Title/TreePath/Status/EmbeddingPending/etc. are touched, so UpdateAsync's SQL
        // rewrites the SAME projection bytes that were already cached, byte for byte, even for a
        // plaintext-changing edit (EmbeddingPending is set true above so the background processor
        // re-embeds it LATER -- the OLD projection stays correctly cached and searchable until then).
        // The one path that actually rewrites projection bytes --
        // EmbeddingProjectionService.ProjectArticleAsync via
        // ArticleRepository.UpdateEmbeddingUnscopedAsync -- already invalidates (incrementally, see
        // EmbeddingVectorCache.UpdateOne) the moment it happens. Invalidating here too used to force a
        // full corpus-wide cache rebuild (~150MB of SQLite reads at 100k articles) on every single
        // edit -- from ~20 people editing constantly, that made the cache "essentially never warm".
        // Do not "restore" this call -- see EmbeddingVectorCache's own doc comment for the full
        // reasoning.
        eventLogger.SignalSync();

        if (plaintext != null)
            await LinkOrphanMediaAsync(id, plaintext);
    }

    /// <summary>
    /// Appends text to the end of an article's body, reading and writing under one lock.
    ///
    /// <para>
    /// Lives here rather than in the calling tool for a reason: a caller that fetched the body
    /// itself and then called <see cref="UpdateAsync"/> would leave a window between the two in
    /// which another writer's change lands and is then overwritten wholesale. With ~20 people plus
    /// agents on one node, two appends arriving together silently dropped one of them — recoverable
    /// only by digging through version history, if anyone noticed at all.
    /// </para>
    /// </summary>
    /// <returns>The new body length, for the caller's confirmation message.</returns>
    public async Task<int> AppendAsync(Guid id, string text)
    {
        using var _ = await ArticleWriteLock.AcquireAsync(id);
        var content = await GetContentAsync(id);
        var newContent = content + "\n\n" + text;
        await UpdateCoreAsync(id, null, null, null, newContent, null, false, false);
        return newContent.Length;
    }

    /// <summary>Prepends text to an article's body. See <see cref="AppendAsync"/> for why it locks.</summary>
    /// <returns>The new body length.</returns>
    public async Task<int> PrependAsync(Guid id, string text)
    {
        using var _ = await ArticleWriteLock.AcquireAsync(id);
        var content = await GetContentAsync(id);
        var newContent = text + "\n\n" + content;
        await UpdateCoreAsync(id, null, null, null, newContent, null, false, false);
        return newContent.Length;
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="search"/> with <paramref name="replace"/>.
    /// See <see cref="AppendAsync"/> for why it locks.
    /// </summary>
    /// <returns>
    /// Occurrences replaced. Zero means the article was left completely untouched — no version
    /// snapshot, no updatedAt change, no event.
    /// </returns>
    public async Task<int> ReplaceInAsync(Guid id, string search, string replace)
    {
        using var _ = await ArticleWriteLock.AcquireAsync(id);
        var content = await GetContentAsync(id);

        int count = 0, idx = 0;
        while ((idx = content.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        if (count == 0) return 0;

        await UpdateCoreAsync(id, null, null, null, content.Replace(search, replace), null, false, false);
        return count;
    }

    /// <summary>Soft-deletes an article.</summary>
    public async Task DeleteAsync(Guid id)
    {
        using var _ = await ArticleWriteLock.AcquireAsync(id);

        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await mediaRepo.SoftDeleteByArticleIdAsync(id, tx);
                // Log first, delete second: the log mints the version and the row has to carry
                // exactly it. Deleting first would mean inventing a version here and hoping the
                // logger picked the same one — which is the bug this ordering removes, not a
                // hypothetical. Both writes are inside the caller's transaction, so nothing is
                // observable in between.
                var version = await eventLogger.LogDeleteAsync(id, tx);
                await articleRepo.SoftDeleteAsync(id, version, tx);

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back; don't mask the real failure */ }
                throw;
            }
        }

        eventLogger.SignalSync();
    }

    /// <summary>List of article metadata, optionally filtered by tree path and/or a strict updatedAfter cutoff.</summary>
    public Task<List<Article>> ListAsync(string? treePath = null, DateTime? updatedAfter = null) => articleRepo.ListAsync(treePath, updatedAfter);

    /// <summary>Moves an article to another folder (tree_path only, no content re-signing).</summary>
    public Task MoveAsync(Guid id, string newPath) => UpdateAsync(id, treePath: newPath);

    public async Task<int> DeleteByPathAsync(string path)
    {
        var articles = await articleRepo.ListAsync(path);
        foreach (var article in articles)
            await DeleteAsync(article.Id);
        return articles.Count;
    }

    private async Task<Folder?> EnsureFolderExistsAsync(string treePath)
    {
        if (string.IsNullOrEmpty(treePath) || treePath == "/") return null;
        await folderRepo.EnsureExistsAsync(treePath, (await nodeRepo.GetAsync())?.NodeId);
        return await folderRepo.GetByPathAsync(treePath);
    }

    private async Task LinkOrphanMediaAsync(Guid articleId, string body)
    {
        var mediaIds = MediaRefRegex().Matches(body)
            .Select(m => Guid.TryParse(m.Groups[1].Value, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (mediaIds.Count == 0) return;

        var lamportTs = clock.Tick();
        var identity = await nodeRepo.GetAsync();
        var linked = await mediaRepo.LinkOrphansToArticleAsync(mediaIds, articleId, lamportTs, identity?.NodeId);
        foreach (var id in linked)
            await eventLogger.LogMediaLinkAsync(id, articleId, lamportTs);
    }
}
