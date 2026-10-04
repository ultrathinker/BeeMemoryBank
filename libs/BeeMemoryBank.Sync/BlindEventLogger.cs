using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The <see cref="IEventLogger"/> of a blind node: it authors nothing. Every Log* method refuses, with the same exception
/// type and wording the full <c>EventLogger</c> used to throw on a blind node ("this is a blind node, and blind nodes never
/// author events"; the source half of the authorship ban, plan 3.2 - EventApplier enforces the receiving half). Keeps a stray
/// local write on a blind node out of its log, where every peer that pulls it would reject it again. The full logger
/// (<c>EventLogger</c>, signs events with the master DEK) is vault code and not part of a blind node.
/// <see cref="SignalSync"/> still wakes the push loop: it authors nothing and the callers expect it to work.
/// </summary>
public sealed class BlindEventLogger(ISyncTrigger syncTrigger) : IEventLogger
{
    private static Exception Refuse(string eventType) => new InvalidOperationException(
        $"Refusing to log a {eventType} event: this is a blind node, and blind nodes never author events.");

    public Task LogCreateAsync(Article article, EncryptedArticleBody body, string[] conceptTags, IDbTransaction? transaction = null) => throw Refuse("article create");
    public Task LogUpdateAsync(Article article, EncryptedArticleBody? body, string[] conceptTags, IDbTransaction? transaction = null) => throw Refuse("article update");
    public Task<RowVersion> LogDeleteAsync(Guid articleId, IDbTransaction? transaction = null) => throw Refuse("article delete");
    public void SignalSync() => syncTrigger.Signal();
    public Task<RowVersion> LogWhitelistAddAsync(WhitelistEntry entry) => throw Refuse("whitelist add");
    public Task<RowVersion> LogWhitelistRevokeAsync(Guid nodeId) => throw Refuse("whitelist revoke");
    public Task<RowVersion> LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName, bool? isSuperadmin = null, string? tlsSpki = null) => throw Refuse("whitelist update");
    public Task LogMasterPasswordChangedAsync(DateTime changedAt) => throw Refuse("master password changed");
    public Task LogCommentCreateAsync(Comment comment) => throw Refuse("comment create");
    public Task LogCommentDeleteAsync(Guid commentId) => throw Refuse("comment delete");
    public Task LogFolderCreateAsync(Folder folder) => throw Refuse("folder create");
    public Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt) => throw Refuse("folder rename");
    public Task<RowVersion> LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt) => throw Refuse("folder delete");
    public Task LogMediaCreateAsync(Media media, byte[] ciphertext, IDbTransaction? transaction = null) => throw Refuse("media create");
    public Task LogMediaDeleteAsync(Guid mediaId, IDbTransaction? transaction = null) => throw Refuse("media delete");
    public Task LogConceptTagRenameAsync(string oldName, string newName) => throw Refuse("concept tag rename");
    public Task LogConceptTagMergeAsync(string source, string target) => throw Refuse("concept tag merge");
    public Task LogConceptTagDeleteAsync(string name) => throw Refuse("concept tag delete");
    public Task LogMediaLinkAsync(Guid mediaId, Guid articleId, long lamportTs) => throw Refuse("media link");
    public Task LogHardDeleteAsync(string entityType, string entityIdentifier) => throw Refuse("hard delete");
    public Task LogSnapshotCheckpointAsync(long cpSeq, int eventsRemoved, string snapshotFileName, string snapshotSha256, string? prevCheckpointSha256, DateTime producedAt) => throw Refuse("snapshot checkpoint");
}
