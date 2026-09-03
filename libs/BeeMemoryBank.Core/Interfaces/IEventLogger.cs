using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Records local operations to the event log.
/// Implementation is in BeeMemoryBank.Sync; null implementation in Core (Phase 1).
/// </summary>
public interface IEventLogger
{
    Task LogCreateAsync(Article article, EncryptedArticleBody body, string[] conceptTags, IDbTransaction? transaction = null);
    Task LogUpdateAsync(Article article, EncryptedArticleBody? body, string[] conceptTags, IDbTransaction? transaction = null);
    Task LogDeleteAsync(Guid articleId, IDbTransaction? transaction = null);

    /// <summary>
    /// Wakes the background sync-push loop. When any of the three log methods above is given a
    /// transaction, it deliberately does NOT self-signal (it would fire before the caller's
    /// transaction commits, waking the push loop to look for a row on another connection that
    /// can't see it yet) — the caller must call this explicitly, once, strictly after its own
    /// <c>tx.Commit()</c> succeeds. Given no transaction, the three log methods self-signal as
    /// before and callers don't need to call this at all.
    /// </summary>
    void SignalSync();
    Task LogWhitelistAddAsync(WhitelistEntry entry);
    Task LogWhitelistRevokeAsync(Guid nodeId);
    Task LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName);
    Task LogCommentCreateAsync(Comment comment);
    Task LogCommentDeleteAsync(Guid commentId);
    Task LogFolderCreateAsync(Folder folder);
    Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt);
    Task LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt);
    Task LogMediaCreateAsync(Media media, byte[] ciphertext, IDbTransaction? transaction = null);
    Task LogMediaDeleteAsync(Guid mediaId);
    Task LogConceptTagRenameAsync(string oldName, string newName);
    Task LogConceptTagMergeAsync(string source, string target);
    Task LogConceptTagDeleteAsync(string name);
    Task LogMediaLinkAsync(Guid mediaId, Guid articleId, long lamportTs);
    Task LogHardDeleteAsync(string entityType, string entityIdentifier);
    Task LogSnapshotCheckpointAsync(long cpSeq, int eventsRemoved, string snapshotFileName, string snapshotSha256, string? prevCheckpointSha256, DateTime producedAt);
}
