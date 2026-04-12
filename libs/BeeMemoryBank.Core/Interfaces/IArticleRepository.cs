using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleRepository
{
    Task<Article?> GetByIdAsync(Guid id, bool includeDeleted = false);
    Task<List<Article>> ListAsync(string? treePath = null);
    Task CreateAsync(Article article);
    Task UpdateAsync(Article article);
    Task SoftDeleteAsync(Guid id);
    Task<List<Article>> SearchAsync(string query);
    Task<List<Article>> GetByIdsAsync(List<Guid> ids);
    Task<List<Article>> GetEmbeddingPendingAsync(int limit = 100);
    Task UpdateEmbeddingAsync(Guid id, byte[] projection, string modelVersion);
    Task<List<Article>> SearchByEmbeddingAsync(float[] queryProjection, int topK = 10);
    Task<List<TagInfo>> GetAllTagsAsync();
    Task<List<Article>> GetRecentActivityAsync(int limit = 50);
    Task SetFolderIdAsync(Guid articleId, Guid folderId);
    Task ClearFolderIdAsync(Guid folderId);
    Task<List<(Guid Id, string TreePath)>> GetArticlesWithNullFolderIdAsync();
}
