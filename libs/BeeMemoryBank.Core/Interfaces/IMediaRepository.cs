using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IMediaRepository
{
    Task<Media?> GetByIdAsync(Guid id, bool includeDeleted = false);
    Task<List<Media>> GetByArticleIdAsync(Guid articleId);
    Task CreateAsync(Media media);
    Task SoftDeleteByArticleIdAsync(Guid articleId);
    Task<List<Media>> GetDeletedOlderThanAsync(DateTime cutoff);
    Task<List<Media>> GetOrphanedOlderThanAsync(DateTime cutoff);
    Task DeleteByIdAsync(Guid id);
    Task SoftDeleteAsync(Guid id);
}
