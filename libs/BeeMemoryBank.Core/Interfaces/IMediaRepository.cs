using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IMediaRepository
{
    Task<Media?> GetByIdAsync(Guid id, bool includeDeleted = false);
    Task<List<Media>> GetByArticleIdAsync(Guid articleId);
    /// <summary>
    /// Same optional-transaction contract as <see cref="IArticleRepository"/>: null (the default)
    /// means this method owns its connection and commits on its own; a non-null transaction means
    /// it executes on that transaction's connection and leaves commit/rollback to the caller.
    /// <c>MediaService.CreateAsync</c> uses this to put the media row and its sync event in one
    /// transaction, so media can never exist locally without the event that propagates it.
    /// </summary>
    Task CreateAsync(Media media, IDbTransaction? transaction = null);
    Task SoftDeleteByArticleIdAsync(Guid articleId, IDbTransaction? transaction = null);
    Task<List<Media>> GetDeletedOlderThanAsync(DateTime cutoff);
    Task<List<Media>> GetOrphanedOlderThanAsync(DateTime cutoff);
    Task DeleteByIdAsync(Guid id);
    Task SoftDeleteAsync(Guid id);
    Task UpdateLamportTsAsync(Guid id, long lamportTs, Guid? sourceNodeId);
    Task<List<Guid>> LinkOrphansToArticleAsync(IEnumerable<Guid> mediaIds, Guid articleId, long lamportTs, Guid? sourceNodeId);
}
