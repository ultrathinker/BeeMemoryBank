using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleBodyRepository
{
    Task<EncryptedArticleBody?> GetByArticleIdAsync(Guid articleId);
    Task<List<EncryptedArticleBody>> GetAllActiveAsync();
    Task UpsertAsync(EncryptedArticleBody body);
    Task<int> GetActiveCountAsync();
    Task<List<EncryptedArticleBody>> GetActiveBatchAsync(int limit, int offset);
    /// <summary>Purges ciphertexts for soft-deleted articles older than cutoff.</summary>
    Task<int> PurgeForDeletedArticlesOlderThanAsync(DateTime cutoff);
}
