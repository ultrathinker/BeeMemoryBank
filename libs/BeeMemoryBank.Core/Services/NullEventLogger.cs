using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// No-op implementation for Phase 1 and tests without sync.
/// </summary>
public sealed class NullEventLogger : IEventLogger
{
    public Task LogCreateAsync(Article article, EncryptedArticleBody body) => Task.CompletedTask;
    public Task LogUpdateAsync(Article article, EncryptedArticleBody? body) => Task.CompletedTask;
    public Task LogDeleteAsync(Guid articleId) => Task.CompletedTask;
    public Task LogWhitelistAddAsync(WhitelistEntry entry) => Task.CompletedTask;
    public Task LogWhitelistRevokeAsync(Guid nodeId) => Task.CompletedTask;
    public Task LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName) => Task.CompletedTask;
    public Task LogCommentCreateAsync(Comment comment) => Task.CompletedTask;
    public Task LogCommentDeleteAsync(Guid commentId) => Task.CompletedTask;
    public Task LogFolderCreateAsync(Folder folder) => Task.CompletedTask;
    public Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt) => Task.CompletedTask;
    public Task LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt) => Task.CompletedTask;
    public Task LogMediaCreateAsync(Media media, byte[] ciphertext) => Task.CompletedTask;
    public Task LogMediaDeleteAsync(Guid mediaId) => Task.CompletedTask;
}
