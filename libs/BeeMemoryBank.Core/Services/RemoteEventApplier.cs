using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Friend-side: applies a snapshot fetched from a remote BMB node to the
/// local replica. Runs under SystemCallerScope so repository write-guards
/// don't refuse the inbound rows (they have remote_subscription_id set,
/// which is a hard refusal for any non-system caller).
///
/// MVP behaviour: every poll is a full snapshot. The applier upserts
/// folders and articles, and soft-deletes any local rows belonging to the
/// subscription that were not present in the snapshot ("disappeared on
/// the source" semantics).
/// </summary>
public class RemoteEventApplier(
    IFolderRepository folderRepo,
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    SessionService session,
    CallerScopeHolder scopeHolder)
{
    public async Task ApplySnapshotAsync(RemoteSubscription sub, RemoteSnapshot snap)
    {
        using var _ = scopeHolder.ElevateToSystem();
        await ApplyInternalAsync(sub, snap);
    }

    private async Task ApplyInternalAsync(RemoteSubscription sub, RemoteSnapshot snap)
    {
        var rootRemote = snap.RootPath.TrimEnd('/');
        var rootLocal = sub.MountPath.TrimEnd('/');

        // 1. Ensure the mount root + all parents exist locally. Ancestor folders
        // stay un-tagged (they're user-organisational containers — surviving
        // subscription detach is fine). The mount-root itself gets tagged with
        // the subscription's remote-origin info in step 3 so cleanup later
        // recognises it as ours.
        await EnsureLocalChainAsync(rootLocal);

        // 2. Build remote-origin → local folder map. Start with the mount root.
        var allFolders = await folderRepo.GetAllActiveAsync();
        // GroupBy.First instead of ToDictionary — a stale row pair with a
        // null/duplicate RemoteOriginId would otherwise crash the entire poll
        // (caught by kilo round-3). First-wins lets the duplicate get cleaned
        // by the orphan pass.
        var existingBySubId = allFolders
            .Where(f => f.RemoteSubscriptionId == sub.Id)
            .GroupBy(f => f.RemoteOriginId ?? "")
            .ToDictionary(g => g.Key, g => g.First());

        // 3. Upsert each remote folder, preserving subtree structure.
        var seenFolderOriginIds = new HashSet<string>();
        foreach (var rf in snap.Folders.OrderBy(f => f.Path.Length))
        {
            var originId = rf.Id.ToString("D");
            seenFolderOriginIds.Add(originId);

            string localPath;
            if (string.Equals(rf.Path, rootRemote, StringComparison.OrdinalIgnoreCase))
            {
                localPath = rootLocal;
            }
            else
            {
                // SECURITY: same path-traversal guard as for articles —
                // a hostile/buggy owner could send a folder Path that escapes
                // the mount root. Canonicalise and skip if out of bounds.
                if (string.IsNullOrEmpty(rf.Path) || !rf.Path.StartsWith(rootRemote, StringComparison.OrdinalIgnoreCase))
                    continue;
                var suffix = rf.Path[rootRemote.Length..];
                var candidate = TreePathCanonicalizer.Canonicalize(rootLocal + suffix);
                var rootLocalPrefix = rootLocal.TrimEnd('/') + "/";
                if (!candidate.Equals(rootLocal, StringComparison.OrdinalIgnoreCase)
                    && !candidate.StartsWith(rootLocalPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                localPath = candidate;
            }

            if (existingBySubId.TryGetValue(originId, out var existing))
            {
                if (existing.Path != localPath)
                {
                    existing.Path = localPath;
                    existing.Name = GetLastSegment(localPath);
                    existing.ParentPath = GetParentPath(localPath);
                    existing.UpdatedAt = DateTime.UtcNow;
                    await folderRepo.UpdateAsync(existing);
                }
            }
            else
            {
                // If a folder already lives at this path (typically the mount
                // root that EnsureLocalChainAsync created un-tagged), upgrade
                // it in-place so cleanup tracks it under this subscription.
                var alreadyAtPath = await folderRepo.GetByPathAsync(localPath);
                if (alreadyAtPath != null)
                {
                    if (!alreadyAtPath.RemoteSubscriptionId.HasValue)
                    {
                        alreadyAtPath.RemoteSubscriptionId = sub.Id;
                        alreadyAtPath.RemoteOriginId = originId;
                        await folderRepo.UpdateAsync(alreadyAtPath);
                    }
                    continue;
                }
                var folder = new Folder
                {
                    Id = Guid.NewGuid(),
                    Path = localPath,
                    Name = GetLastSegment(localPath),
                    ParentPath = GetParentPath(localPath),
                    Status = "A",
                    LamportTs = clock.Tick(),
                    SourceNodeId = (await nodeRepo.GetAsync())?.NodeId,
                    CreatedAt = rf.CreatedAt == default ? DateTime.UtcNow : rf.CreatedAt,
                    UpdatedAt = rf.UpdatedAt == default ? DateTime.UtcNow : rf.UpdatedAt,
                    RemoteSubscriptionId = sub.Id,
                    RemoteOriginId = originId
                };
                await folderRepo.CreateAsync(folder);
            }
        }

        // 4. Upsert each remote article.
        var allLocal = await articleRepo.ListAsync();
        var existingArticlesBySubId = allLocal
            .Where(a => a.RemoteSubscriptionId == sub.Id)
            .GroupBy(a => a.RemoteOriginId ?? "")
            .ToDictionary(g => g.Key, g => g.First());

        var seenArticleOriginIds = new HashSet<string>();
        foreach (var ra in snap.Articles)
        {
            var originId = ra.Id.ToString("D");
            seenArticleOriginIds.Add(originId);

            // SECURITY: the remote node could send a malicious TreePath like
            // "/Recipes/../../Admin/Secrets" — naïve concatenation would let
            // it escape the mount root. Canonicalise and verify containment.
            // Gemini security review 2026-05-25.
            if (string.IsNullOrEmpty(ra.TreePath) || !ra.TreePath.StartsWith(rootRemote, StringComparison.OrdinalIgnoreCase))
            {
                // Either malformed or outside the share subtree → skip.
                continue;
            }
            var suffix = ra.TreePath.Length > rootRemote.Length ? ra.TreePath[rootRemote.Length..] : "";
            var candidate = TreePathCanonicalizer.Canonicalize(rootLocal + suffix);
            var rootLocalPrefix = rootLocal.TrimEnd('/') + "/";
            if (!candidate.Equals(rootLocal, StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith(rootLocalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Path traversal attempt — drop the record.
                continue;
            }
            var localPath = candidate;
            var content = ra.Content ?? string.Empty;

            if (existingArticlesBySubId.TryGetValue(originId, out var existing))
            {
                // Update only if remote_version advanced.
                if ((ra.LamportTs ?? 0) > (existing.RemoteVersion ?? 0)
                    || existing.Title != ra.Title
                    || existing.TreePath != localPath)
                {
                    existing.Title = ra.Title;
                    existing.TreePath = localPath;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.RemoteVersion = ra.LamportTs;
                    existing.RemoteUpdatedBy = ra.UpdatedBy;
                    // Mirror the protected flag from the (already-encrypted) remote body so the local
                    // copy shows the lock card instead of a raw BMBENC1 blob.
                    existing.Protected = Crypto.ProtectedContentCodec.IsProtected(content);
                    await UpsertEncryptedBodyAsync(existing.Id, content);
                    await articleRepo.UpdateAsync(existing);
                }
            }
            else
            {
                var newArticle = await CreateMirroredArticleAsync(sub, ra, localPath, content);
            }
        }

        // 5. Cleanup: soft-delete local rows belonging to this subscription that
        // disappeared from the snapshot (article first, then folders deepest-first).
        //
        // SAFETY-NET: if the incoming snapshot would wipe out >50 % of mirrored
        // articles, refuse the cleanup pass and emit a warning. This protects
        // against owner-side bugs / partial responses that nevertheless arrived
        // as valid JSON (e.g. Articles array silently truncated). Worst case
        // outcome: next poll re-syncs cleanly. The alternative — quiet mass
        // delete — was kilo's critical finding in 2026-05-24 review.
        // Count only existing rows the snapshot did NOT mention — net difference
        // (Count - SeenCount) silently misses the case "owner deleted 10, added
        // 10 different ones" → 0 net but 10 actual losses. Bug caught by gemini
        // round-3 review. Always count via Keys.Count(k => !seen.Contains(k)).
        var articlesGoingAway = existingArticlesBySubId.Keys.Count(k => !seenArticleOriginIds.Contains(k));
        var safeToCleanArticles = existingArticlesBySubId.Count == 0
            || articlesGoingAway <= existingArticlesBySubId.Count / 2;
        if (!safeToCleanArticles)
        {
            return; // require manual intervention rather than silent mass delete
        }

        // These rows mirror another node's content and are not replicated onward, so no event is
        // written for them — but they still sit in replicated tables, so they still need a version
        // a comparison can be made against. A fresh local tick is the honest one: this node did
        // just decide, now, that the row is gone.
        var identity = await nodeRepo.GetAsync();
        foreach (var orphan in existingArticlesBySubId.Where(kv => !seenArticleOriginIds.Contains(kv.Key)).Select(kv => kv.Value))
        {
            await articleRepo.SoftDeleteAsync(orphan.Id, RowVersion.Of(clock.Tick(), identity?.NodeId));
        }
        foreach (var orphan in existingBySubId
                     .Where(kv => !seenFolderOriginIds.Contains(kv.Key))
                     .Select(kv => kv.Value)
                     .OrderByDescending(f => f.Path.Length))
        {
            await folderRepo.SoftDeleteAsync(orphan.Id, DateTime.UtcNow);
            await folderRepo.SetDeleteVersionAsync(orphan.Id, RowVersion.Of(clock.Tick(), identity?.NodeId));
        }
    }

    private async Task<Article> CreateMirroredArticleAsync(RemoteSubscription sub, RemoteSnapshotArticle ra, string localPath, string content)
    {
        // Ensure the article's folder exists locally.
        await EnsureLocalChainAsync(localPath);

        var folder = await folderRepo.GetByPathAsync(localPath);

        var articleId = Guid.NewGuid();
        var masterDek = session.GetMasterDek();
        byte[] ciphertext, iv, encryptedDek, dekIv;
        try
        {
            var articleDek = DekManager.GenerateArticleDek();
            try
            {
                var dekAad = "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();
                var bodyAad = "bmb-art-body"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();
                (ciphertext, iv) = ArticleEncryptor.Encrypt(content, articleDek, bodyAad);
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

        var article = new Article
        {
            Id = articleId,
            Title = ra.Title,
            TreePath = localPath,
            FolderId = folder?.Id,
            Status = "A",
            LamportTs = clock.Tick(),
            SourceNodeId = (await nodeRepo.GetAsync())?.NodeId,
            CreatedAt = ra.CreatedAt == default ? DateTime.UtcNow : ra.CreatedAt,
            UpdatedAt = ra.UpdatedAt == default ? DateTime.UtcNow : ra.UpdatedAt,
            RemoteSubscriptionId = sub.Id,
            RemoteOriginId = ra.Id.ToString("D"),
            RemoteVersion = ra.LamportTs,
            RemoteUpdatedBy = ra.UpdatedBy,
            Protected = Crypto.ProtectedContentCodec.IsProtected(content)
        };
        await articleRepo.CreateAsync(article);

        await bodyRepo.UpsertAsync(new EncryptedArticleBody
        {
            ArticleId = articleId,
            Ciphertext = ciphertext,
            IV = iv,
            EncryptedDek = encryptedDek,
            DekIV = dekIv
        });
        return article;
    }

    private async Task UpsertEncryptedBodyAsync(Guid articleId, string plaintext)
    {
        var body = await bodyRepo.GetByArticleIdAsync(articleId);
        var masterDek = session.GetMasterDek();
        byte[] ciphertext, iv, encryptedDek, dekIv;
        byte[] articleDek;
        try
        {
            if (body != null)
            {
                var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
                var unwrapAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray() : null;
                articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, unwrapAad);
            }
            else
            {
                articleDek = DekManager.GenerateArticleDek();
            }
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

        await bodyRepo.UpsertAsync(new EncryptedArticleBody
        {
            ArticleId = articleId,
            Ciphertext = ciphertext,
            IV = iv,
            EncryptedDek = encryptedDek,
            DekIV = dekIv
        });
    }

    private async Task EnsureLocalChainAsync(string path)
    {
        // Walk from root down, creating each missing segment.
        if (path == "/" || string.IsNullOrEmpty(path)) return;
        var parts = path.Trim('/').Split('/');
        var sb = "";
        foreach (var p in parts)
        {
            sb += "/" + p;
            var existing = await folderRepo.GetByPathAsync(sb);
            if (existing == null)
            {
                var f = new Folder
                {
                    Id = Guid.NewGuid(),
                    Path = sb,
                    Name = p,
                    ParentPath = GetParentPath(sb),
                    Status = "A",
                    LamportTs = clock.Tick(),
                    SourceNodeId = (await nodeRepo.GetAsync())?.NodeId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await folderRepo.CreateAsync(f);
            }
        }
    }

    private static string? GetParentPath(string path)
    {
        if (path == "/" || string.IsNullOrEmpty(path)) return null;
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? null : trimmed[..idx];
    }

    private static string GetLastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed.TrimStart('/') : trimmed[(idx + 1)..];
    }
}

public record RemoteSnapshot(
    string RootPath,
    List<RemoteSnapshotFolder> Folders,
    List<RemoteSnapshotArticle> Articles,
    long Cursor);

public record RemoteSnapshotFolder(
    Guid Id, string Path, string Name, string? ParentPath,
    long LamportTs, DateTime CreatedAt, DateTime UpdatedAt);

public record RemoteSnapshotArticle(
    Guid Id, string Title, string TreePath, string? Content,
    long? LamportTs, DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy);
