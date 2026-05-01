using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ICommentRepository
{
    Task<Comment?> GetByIdAsync(int id);
    Task<List<Comment>> GetByArticleIdAsync(Guid articleId);
    Task<Comment> CreateAsync(Guid articleId, string text, Guid? sourceNodeId = null);
    Task<Comment> CreateEncryptedAsync(Guid articleId, Guid commentId, byte[] ciphertext, byte[] iv, Guid? sourceNodeId = null);
    Task<Comment?> GetByCommentIdAsync(Guid commentId);
    Task CreateFromSyncAsync(Comment comment);
    Task UpdateLamportTsAsync(Guid commentId, long lamportTs, Guid? sourceNodeId);
    Task DeleteAsync(int id);

    /// <summary>Soft-deletes an existing comment row (sets deleted_at + delete_lamport_ts).</summary>
    Task SoftDeleteAsync(Guid commentId, long lamportTs, Guid? sourceNodeId);

    /// <summary>
    /// Inserts a placeholder ghost row for a comment that does not yet exist locally.
    /// Used when CommentDelete arrives before CommentCreate (out-of-order delivery).
    /// INSERT OR IGNORE — does nothing if a row already exists.
    /// </summary>
    Task SoftDeletePlaceholderAsync(Guid commentId, long lamportTs, Guid? sourceNodeId);

    /// <summary>
    /// Clears soft-delete and updates all fields from sync data.
    /// Used when a CommentCreate with higher lamport beats an existing soft-deleted row (create wins LWW).
    /// </summary>
    Task ResurrectFromSyncAsync(Guid commentId, Comment data);

    /// <summary>Hard-deletes soft-deleted comments older than the given cutoff. Returns count deleted.</summary>
    Task<int> PurgeSoftDeletedOlderThanAsync(DateTime cutoff);
}
