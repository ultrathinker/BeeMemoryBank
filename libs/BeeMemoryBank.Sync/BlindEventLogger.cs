using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The <see cref="IEventLogger"/> of a blind node: it authors nothing. Every Log* method refuses, with the same exception
/// type and wording the full <c>EventLogger</c> used to throw on a blind node ("this is a blind node, and blind nodes never
/// author events"; the source half of the authorship ban, plan 3.2 - EventApplier enforces the receiving half), and, like the
/// <c>async</c> methods it replaces, it reports the refusal through the returned task (a faulted task), not by throwing at the call.
/// Keeps a stray local write on a blind node out of its log, where every peer that pulls it would reject it again. The full logger
/// (<c>EventLogger</c>, signs events with the master DEK) is vault code and not part of a blind node.
/// <see cref="SignalSync"/> still wakes the push loop: it authors nothing and the callers expect it to work.
/// </summary>
public sealed class BlindEventLogger(ISyncTrigger syncTrigger) : IEventLogger
{
    private static InvalidOperationException Refuse(string eventType) => new(
        $"Refusing to log a {eventType} event: this is a blind node, and blind nodes never author events.");

    private static Task Refused(string eventType) => Task.FromException(Refuse(eventType));
    private static Task<T> Refused<T>(string eventType) => Task.FromException<T>(Refuse(eventType));

    public Task LogCreateAsync(Article article, EncryptedArticleBody body, string[] conceptTags, IDbTransaction? transaction = null) => Refused(EventTypes.ArticleCreate);
    public Task LogUpdateAsync(Article article, EncryptedArticleBody? body, string[] conceptTags, IDbTransaction? transaction = null) => Refused(EventTypes.ArticleUpdate);
    public Task<RowVersion> LogDeleteAsync(Guid articleId, IDbTransaction? transaction = null) => Refused<RowVersion>(EventTypes.ArticleDelete);
    public void SignalSync() => syncTrigger.Signal();
    public Task<RowVersion> LogWhitelistAddAsync(WhitelistEntry entry) => Refused<RowVersion>(EventTypes.WhitelistAdd);
    public Task<RowVersion> LogWhitelistRevokeAsync(Guid nodeId) => Refused<RowVersion>(EventTypes.WhitelistRevoke);
    public Task<RowVersion> LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName, bool? isSuperadmin = null, string? tlsSpki = null, string? tlsTrust = null) => Refused<RowVersion>(EventTypes.WhitelistUpdate);
    public Task LogMasterPasswordChangedAsync(DateTime changedAt) => Refused(EventTypes.MasterPasswordChanged);
    public Task LogCommentCreateAsync(Comment comment) => Refused(EventTypes.CommentCreate);
    public Task LogCommentDeleteAsync(Guid commentId) => Refused(EventTypes.CommentDelete);
    public Task LogFolderCreateAsync(Folder folder) => Refused(EventTypes.FolderCreate);
    public Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt) => Refused(EventTypes.FolderRename);
    public Task<RowVersion> LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt) => Refused<RowVersion>(EventTypes.FolderDelete);
    public Task LogMediaCreateAsync(Media media, byte[] ciphertext, IDbTransaction? transaction = null) => Refused(EventTypes.MediaCreate);
    public Task LogMediaDeleteAsync(Guid mediaId, IDbTransaction? transaction = null) => Refused(EventTypes.MediaDelete);
    public Task LogConceptTagRenameAsync(string oldName, string newName) => Refused(EventTypes.ConceptTagRename);
    public Task LogConceptTagMergeAsync(string source, string target) => Refused(EventTypes.ConceptTagMerge);
    public Task LogConceptTagDeleteAsync(string name) => Refused(EventTypes.ConceptTagDelete);
    public Task LogMediaLinkAsync(Guid mediaId, Guid articleId, long lamportTs) => Refused(EventTypes.MediaLink);
    public Task LogHardDeleteAsync(string entityType, string entityIdentifier) => Refused(EventTypes.HardDelete);
    public Task LogSnapshotCheckpointAsync(long cpSeq, int eventsRemoved, string snapshotFileName, string snapshotSha256, string? prevCheckpointSha256, DateTime producedAt) => Refused(EventTypes.SnapshotCheckpoint);
}
