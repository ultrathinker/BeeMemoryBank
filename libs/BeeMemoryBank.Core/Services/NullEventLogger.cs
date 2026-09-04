using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// No-op implementation for Phase 1 and tests without sync.
/// </summary>
public sealed class NullEventLogger : IEventLogger
{
    public Task LogCreateAsync(Article article, EncryptedArticleBody body, string[] conceptTags, IDbTransaction? transaction = null) => Task.CompletedTask;
    public Task LogUpdateAsync(Article article, EncryptedArticleBody? body, string[] conceptTags, IDbTransaction? transaction = null) => Task.CompletedTask;
    public Task<RowVersion> LogDeleteAsync(Guid articleId, IDbTransaction? transaction = null) => Task.FromResult(default(RowVersion));
    public void SignalSync() { }
    public Task<RowVersion> LogWhitelistAddAsync(WhitelistEntry entry) => Task.FromResult(default(RowVersion));
    public Task<RowVersion> LogWhitelistRevokeAsync(Guid nodeId) => Task.FromResult(default(RowVersion));
    public Task<RowVersion> LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName, bool? isSuperadmin = null) => Task.FromResult(default(RowVersion));
    public Task LogMasterPasswordChangedAsync(DateTime changedAt) => Task.CompletedTask;
    public Task LogCommentCreateAsync(Comment comment) => Task.CompletedTask;
    public Task LogCommentDeleteAsync(Guid commentId) => Task.CompletedTask;
    public Task LogFolderCreateAsync(Folder folder) => Task.CompletedTask;
    public Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt) => Task.CompletedTask;
    public Task<RowVersion> LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt) => Task.FromResult(default(RowVersion));
    public Task LogMediaCreateAsync(Media media, byte[] ciphertext, System.Data.IDbTransaction? transaction = null) => Task.CompletedTask;
    public Task LogMediaDeleteAsync(Guid mediaId) => Task.CompletedTask;
    public Task LogConceptTagRenameAsync(string oldName, string newName) => Task.CompletedTask;
    public Task LogConceptTagMergeAsync(string source, string target) => Task.CompletedTask;
    public Task LogConceptTagDeleteAsync(string name) => Task.CompletedTask;
    public Task LogMediaLinkAsync(Guid mediaId, Guid articleId, long lamportTs) => Task.CompletedTask;
    public Task LogHardDeleteAsync(string entityType, string entityIdentifier) => Task.CompletedTask;
    public Task LogSnapshotCheckpointAsync(long cpSeq, int eventsRemoved, string snapshotFileName, string snapshotSha256, string? prevCheckpointSha256, DateTime producedAt) => Task.CompletedTask;
}
