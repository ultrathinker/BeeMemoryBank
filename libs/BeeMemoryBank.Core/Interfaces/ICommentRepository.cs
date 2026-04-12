using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ICommentRepository
{
    Task<Comment?> GetByIdAsync(int id);
    Task<List<Comment>> GetByArticleIdAsync(Guid articleId);
    Task<Comment> CreateAsync(Guid articleId, string text, Guid? sourceNodeId = null);
    Task<Comment> CreateEncryptedAsync(Guid articleId, byte[] ciphertext, byte[] iv, Guid? sourceNodeId = null);
    Task<bool> ExistsByCommentIdAsync(Guid commentId);
    Task CreateFromSyncAsync(Comment comment);
    Task DeleteAsync(int id);
    Task DeleteByCommentIdAsync(Guid commentId);
}
