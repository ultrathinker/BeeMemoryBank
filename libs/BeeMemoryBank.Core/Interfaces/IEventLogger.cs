using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Records local operations to the event log.
/// Implementation is in BeeMemoryBank.Sync; null implementation in Core (Phase 1).
/// </summary>
public interface IEventLogger
{
    Task LogCreateAsync(Article article, EncryptedArticleBody body);
    Task LogUpdateAsync(Article article, EncryptedArticleBody? body);
    Task LogDeleteAsync(Guid articleId);
    Task LogWhitelistAddAsync(WhitelistEntry entry);
    Task LogWhitelistRevokeAsync(Guid nodeId);
    Task LogWhitelistUpdateAsync(Guid nodeId, string? apiAddress, string? displayName);
    Task LogCommentCreateAsync(Comment comment);
    Task LogCommentDeleteAsync(Guid commentId);
    Task LogFolderCreateAsync(Folder folder);
    Task LogFolderRenameAsync(Guid folderId, string oldPath, string newPath, string newName, string? newParentPath, long lamportTs, DateTime updatedAt);
    Task LogFolderDeleteAsync(Guid folderId, string path, DateTime deletedAt);
    Task LogMediaCreateAsync(Media media, byte[] ciphertext);
    Task LogMediaDeleteAsync(Guid mediaId);
}
