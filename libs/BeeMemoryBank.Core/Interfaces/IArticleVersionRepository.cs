using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleVersionRepository
{
    Task<List<ArticleVersion>> GetByArticleIdAsync(Guid articleId);
    Task<ArticleVersion?> GetAsync(Guid articleId, int versionNumber);
    Task<int> GetMaxVersionNumberAsync(Guid articleId);
    Task CreateAsync(ArticleVersion version);
    Task DeleteOldVersionsAsync(Guid articleId, int keepCount);
}
