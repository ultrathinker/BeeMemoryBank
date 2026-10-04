using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleVersionRepository
{
    Task<List<ArticleVersion>> GetByArticleIdAsync(Guid articleId);
    Task<ArticleVersion?> GetAsync(Guid articleId, int versionNumber);
    Task<ArticleVersion?> GetEarliestAfterAsync(Guid articleId, DateTime baselineAt);
    Task<int> GetMaxVersionNumberAsync(Guid articleId, IDbTransaction? transaction = null);
    Task CreateAsync(ArticleVersion version, IDbTransaction? transaction = null);
    Task DeleteOldVersionsAsync(Guid articleId, int keepCount, IDbTransaction? transaction = null);
}
