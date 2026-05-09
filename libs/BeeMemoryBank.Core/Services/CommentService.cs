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

        var commentId = Guid.NewGuid();
        var masterDek = session.GetMasterDek();
        byte[] ciphertext, iv;
        try
        {
            var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
            var unwrapAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray() : null;
            var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, unwrapAad);
            try
            {
                var commentAad = "bmb-comment"u8.ToArray()
                    .Concat(articleId.ToByteArray())
                    .Concat(commentId.ToByteArray()).ToArray();
                (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek, commentAad);
            }
            finally
            {
                Array.Clear(articleDek);
            }
        }
        finally
        {
            Array.Clear(masterDek);
        }

        var comment = await commentRepo.CreateEncryptedAsync(articleId, commentId, ciphertext, iv);
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

        var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
        var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(comment.ArticleId.ToByteArray()).ToArray() : null;

        var articleDek = session.TryUnwrapWithCandidates(masterDek =>
            DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, dekAad));
        try
        {
            var commentAad = isV1
                ? "bmb-comment"u8.ToArray()
                    .Concat(comment.ArticleId.ToByteArray())
                    .Concat(comment.CommentId.ToByteArray()).ToArray()
                : null;
            return ArticleEncryptor.Decrypt(comment.Ciphertext, comment.IV, articleDek, commentAad);
        }
        finally
        {
            Array.Clear(articleDek);
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
