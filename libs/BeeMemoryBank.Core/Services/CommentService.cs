using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages comments with E2E encryption using the parent article's DEK.
/// </summary>
public class CommentService(
    ICommentRepository commentRepo,
    IArticleBodyRepository bodyRepo,
    SessionService session,
    IEventLogger eventLogger)
{
    /// <summary>Creates an encrypted comment.</summary>
    public async Task<Comment> CreateAsync(Guid articleId, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new ArgumentException("Text is required");

        var body = await bodyRepo.GetByArticleIdAsync(articleId)
            ?? throw new KeyNotFoundException($"Article body {articleId} not found — cannot encrypt comment.");

        var masterDek = session.GetMasterDek();
        byte[] ciphertext, iv;
        try
        {
            var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek);
            (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek);
            Array.Clear(articleDek);
        }
        finally
        {
            Array.Clear(masterDek);
        }

        var comment = await commentRepo.CreateEncryptedAsync(articleId, ciphertext, iv);
        await eventLogger.LogCommentCreateAsync(comment);
        return comment;
    }

    /// <summary>Decrypts and returns comment text.</summary>
    public async Task<string> DecryptTextAsync(Comment comment)
    {
        if (!comment.Encrypted)
            return comment.Text;

        if (comment.Ciphertext == null || comment.IV == null)
            return comment.Text;

        var body = await bodyRepo.GetByArticleIdAsync(comment.ArticleId);
        if (body == null)
            return "[encrypted — article key unavailable]";

        var masterDek = session.GetMasterDek();
        try
        {
            var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek);
            var plaintext = ArticleEncryptor.Decrypt(comment.Ciphertext, comment.IV, articleDek);
            Array.Clear(articleDek);
            return plaintext;
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    /// <summary>Gets comments for an article, decrypting encrypted ones.</summary>
    public async Task<List<(Comment comment, string text)>> GetDecryptedByArticleAsync(Guid articleId)
    {
        var comments = await commentRepo.GetByArticleIdAsync(articleId);
        var result = new List<(Comment, string)>();

        foreach (var c in comments)
        {
            var text = await DecryptTextAsync(c);
            result.Add((c, text));
        }

        return result;
    }

    /// <summary>Deletes a comment by internal id.</summary>
    public async Task DeleteAsync(int id)
    {
        var comment = await commentRepo.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Comment {id} not found");
        await commentRepo.DeleteAsync(id);
        await eventLogger.LogCommentDeleteAsync(comment.CommentId);
    }
}
