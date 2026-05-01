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
    ConceptTagService conceptTagService)
{
    [GeneratedRegex(@"!\[[^\]]*\]\(/api/media/([0-9a-fA-F-]{36})\)")]
    private static partial Regex MediaRefRegex();

    /// <summary>
    /// Creates an article with encrypted body.
    /// Generates per-article DEK, encrypts body, saves both layers, writes to event log.
    /// </summary>
    public async Task<Article> CreateAsync(string title, string treePath, List<string> tags, string plaintext)
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
            UpdatedAt = now
        };

        var folder = await EnsureFolderExistsAsync(treePath);
        article.FolderId = folder?.Id;

        await articleRepo.CreateAsync(article);

        var body = new EncryptedArticleBody
        {
            ArticleId = article.Id,
            Ciphertext = ciphertext,
            IV = iv,
            EncryptedDek = encryptedDek,
            DekIV = dekIv
        };
        await bodyRepo.UpsertAsync(body);

        if (tags.Count > 0)
        {
            await conceptTagService.SetForArticleAsync(article.Id, tags);
        }

        var conceptTags = tags.Count > 0 ? tags.ToArray() : (await conceptTagService.GetByArticleIdAsync(article.Id)).ToArray();
        await eventLogger.LogCreateAsync(article, body, conceptTags);

        await LinkOrphanMediaAsync(article.Id, plaintext);

        return article;
    }

    /// <summary>Returns metadata without decrypting the body.</summary>
    public Task<Article?> GetMetadataAsync(Guid id) => articleRepo.GetByIdAsync(id);

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
        string? plaintext = null)
    {
        var article = await articleRepo.GetByIdAsync(id)
                       ?? throw new KeyNotFoundException($"Article {id} not found.");

        var prevTitle = article.Title;
        var prevTreePath = article.TreePath;

        if (title != null) article.Title = title;
        if (treePath != null)
        {
            treePath = TreePathCanonicalizer.Canonicalize(treePath);
            var folder = await EnsureFolderExistsAsync(treePath);
            article.FolderId = folder?.Id;
            article.TreePath = treePath;
        }

        var identity = await nodeRepo.GetAsync();
        article.LamportTs = clock.Tick();
        article.SourceNodeId = identity?.NodeId;
        article.UpdatedAt = DateTime.UtcNow;

        await articleRepo.UpdateAsync(article);

        if (tags != null)
        {
            await conceptTagService.SetForArticleAsync(id, tags);
        }

        EncryptedArticleBody? body = null;
        if (plaintext != null)
        {
            body = await bodyRepo.GetByArticleIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article body {id} not found.");

            var maxVer = await versionRepo.GetMaxVersionNumberAsync(id);
            var actorName = actorProvider.ActorName ?? actorProvider.ActorType;
            var nodeDisplayName = identity?.DisplayName;
            var updatedBy = nodeDisplayName != null
                ? $"{nodeDisplayName} / {actorName}"
                : actorName;
            await versionRepo.CreateAsync(new ArticleVersion
            {
                Id = Guid.NewGuid(),
                ArticleId = id,
                VersionNumber = maxVer + 1,
                Title = prevTitle,
                TreePath = prevTreePath,
                Ciphertext = body.Ciphertext,
                IV = body.IV,
                EncryptedDek = body.EncryptedDek,
                DekIV = body.DekIV,
                UpdatedBy = updatedBy,
                CreatedAt = DateTime.UtcNow
            });
            await versionRepo.DeleteOldVersionsAsync(id, 50);

            var masterDek = session.GetMasterDek();
            try
            {
                var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
                var unwrapAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
                var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, unwrapAad);
                try
                {
                    var dekAad = "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray();
                    var bodyAad = "bmb-art-body"u8.ToArray().Concat(id.ToByteArray()).ToArray();
                    var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek, bodyAad);
                    var (encryptedDek, dekIv) = DekManager.WrapDek(articleDek, masterDek, dekAad);
                    body.Ciphertext = ciphertext;
                    body.IV = iv;
                    body.EncryptedDek = encryptedDek;
                    body.DekIV = dekIv;
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
            await bodyRepo.UpsertAsync(body);
        }
        else
        {
            body = await bodyRepo.GetByArticleIdAsync(id);
        }

        var conceptTags = await conceptTagService.GetByArticleIdAsync(id);
        await eventLogger.LogUpdateAsync(article, body, conceptTags.ToArray());

        if (plaintext != null)
            await LinkOrphanMediaAsync(id, plaintext);
    }

    /// <summary>Soft-deletes an article.</summary>
    public async Task DeleteAsync(Guid id)
    {
        await mediaRepo.SoftDeleteByArticleIdAsync(id);
        await articleRepo.SoftDeleteAsync(id);
        await eventLogger.LogDeleteAsync(id);
    }

    /// <summary>List of article metadata, optionally filtered by tree path.</summary>
    public Task<List<Article>> ListAsync(string? treePath = null) => articleRepo.ListAsync(treePath);

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
