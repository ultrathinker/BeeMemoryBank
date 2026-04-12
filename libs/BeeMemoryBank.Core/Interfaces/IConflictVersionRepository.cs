using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IConflictVersionRepository
{
    Task CreateAsync(ConflictVersion conflict);
    Task<List<ConflictVersion>> GetByArticleIdAsync(Guid articleId);
    /// <summary>Deletes expired conflict versions.</summary>
    Task<int> DeleteExpiredAsync(DateTime now);
}
