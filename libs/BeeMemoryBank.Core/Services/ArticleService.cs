using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

public class ArticleService(
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    SessionService session,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    IEventLogger eventLogger,
    IMediaRepository mediaRepo,
    IFolderRepository folderRepo,
    IArticleVersionRepository versionRepo,
    IActorProvider actorProvider)
{
    /// <summary>
    /// Creates an article with encrypted body.
    /// Generates per-article DEK, encrypts body, saves both layers, writes to event log.
    /// </summary>
    public async Task<Article> CreateAsync(string title, string treePath, List<string> tags, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title cannot be empty.");
        if (string.IsNullOrWhiteSpace(treePath) || !treePath.StartsWith('/'))
            throw new ArgumentException("Path must start with '/'.");

        var masterDek = session.GetMasterDek();
        byte[] ciphertext, iv, encryptedDek, dekIv;
        try
        {
            var articleDek = DekManager.GenerateArticleDek();
            (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek);
            (encryptedDek, dekIv) = DekManager.WrapDek(articleDek, masterDek);
            Array.Clear(articleDek);
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
            Id = Guid.NewGuid(),
            Title = title,
            TreePath = treePath,
            Tags = tags,
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
        await eventLogger.LogCreateAsync(article, body);

        return article;
    }

    /// <summary>Returns metadata without decrypting the body.</summary>
    public Task<Article?> GetMetadataAsync(Guid id) => articleRepo.GetByIdAsync(id);

    /// <summary>Decrypts and returns the article body. Requires an unlocked session.</summary>
    public async Task<string> GetContentAsync(Guid id)
    {
        var body = await bodyRepo.GetByArticleIdAsync(id);
        if (body == null) throw new KeyNotFoundException($"Article body {id} not found.");

        var masterDek = session.GetMasterDek();
        try
        {
            var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek);
            var plaintext = ArticleEncryptor.Decrypt(body.Ciphertext, body.IV, articleDek);
            Array.Clear(articleDek);
            return plaintext;
        }
        finally
        {
            Array.Clear(masterDek);
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

        // Capture pre-update metadata for version history (used only when body changes)
        var prevTitle = article.Title;
        var prevTags = article.Tags.ToList();
        var prevTreePath = article.TreePath;

        if (title != null) article.Title = title;
        if (treePath != null)
        {
            var folder = await EnsureFolderExistsAsync(treePath);
            article.FolderId = folder?.Id;
            article.TreePath = treePath;
        }
        if (tags != null) article.Tags = tags;

        var identity = await nodeRepo.GetAsync();
        article.LamportTs = clock.Tick();
        article.SourceNodeId = identity?.NodeId;
        article.UpdatedAt = DateTime.UtcNow;

        await articleRepo.UpdateAsync(article);

        EncryptedArticleBody? body = null;
        if (plaintext != null)
        {
            body = await bodyRepo.GetByArticleIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article body {id} not found.");

            var maxVer = await versionRepo.GetMaxVersionNumberAsync(id);
            var updatedBy = actorProvider.ActorName ?? actorProvider.ActorType;
            await versionRepo.CreateAsync(new ArticleVersion
            {
                Id = Guid.NewGuid(),
                ArticleId = id,
                VersionNumber = maxVer + 1,
                Title = prevTitle,
                Tags = prevTags,
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
                // Use the same article DEK but a new IV
                var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek);
                var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek);
                Array.Clear(articleDek);
                body.Ciphertext = ciphertext;
                body.IV = iv;
            }
            finally
            {
                Array.Clear(masterDek);
            }
            await bodyRepo.UpsertAsync(body);
        }
        else
        {
            // Metadata-only update: load body for inclusion in event log
            body = await bodyRepo.GetByArticleIdAsync(id);
        }

        await eventLogger.LogUpdateAsync(article, body);
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
}
