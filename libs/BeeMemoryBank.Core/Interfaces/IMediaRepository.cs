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

    /// <summary>
    /// Links still-unlinked, active ATTACHMENT rows to <paramref name="articleId"/>. When
    /// <paramref name="uploadedBy"/> is non-null only rows uploaded by that owner key are taken;
    /// null (superadmin) takes any. Returns the ids actually linked.
    /// </summary>
    Task<List<Guid>> LinkOrphanAttachmentsAsync(IEnumerable<Guid> mediaIds, Guid articleId, string? uploadedBy, long lamportTs, Guid? sourceNodeId);

    /// <summary>
    /// The row <paramref name="id"/> if it is active, unlinked and uploaded by
    /// <paramref name="uploadedBy"/>; else null. Deliberately NOT scoped by the caller's folder
    /// ACL: an unlinked row has no folder, so the uploader is the only thing that can own it.
    /// </summary>
    Task<Media?> GetOwnedOrphanAsync(Guid id, string uploadedBy);

    /// <summary>
    /// Soft-deletes <paramref name="id"/> only if it is still active, unlinked and uploaded by
    /// <paramref name="uploadedBy"/> — checked and written in one statement, inside the caller's
    /// transaction so the media-delete event can commit with it. True if a row changed.
    /// </summary>
    Task<bool> SoftDeleteOwnedOrphanAsync(Guid id, string uploadedBy, IDbTransaction transaction);
}
