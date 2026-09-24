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
        byte[] ciphertext, iv;
        // Candidates, not just the current key: the article body may still be wrapped under a
        // retired master DEK right after a rotation, and GetContentAsync reads it fine then.
        var articleDek = session.TryUnwrapWithCandidates(masterDek =>
            EnvelopeFraming.Article.UnwrapDek(articleId, body.EncryptedDek, body.DekIV, masterDek));
        try
        {
            (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek, CommentAad(articleId, commentId));
        }
        finally
        {
            Array.Clear(articleDek);
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

        var articleDek = session.TryUnwrapWithCandidates(masterDek =>
            EnvelopeFraming.Article.UnwrapDek(comment.ArticleId, body.EncryptedDek, body.DekIV, masterDek));
        try
        {
            // A comment row has no framing marker of its own; the reader infers it from the parent
            // article body's framing (v1 body → comment sealed with AAD, v0 → without).
            var commentAad = EnvelopeFraming.IsVersioned(body.EncryptedDek)
                ? CommentAad(comment.ArticleId, comment.CommentId)
                : null;
            return ArticleEncryptor.Decrypt(comment.Ciphertext, comment.IV, articleDek, commentAad);
        }
        finally
        {
            Array.Clear(articleDek);
        }
    }

    /// <summary>AAD a comment is sealed under: <c>"bmb-comment" || articleId || commentId</c>.</summary>
    private static byte[] CommentAad(Guid articleId, Guid commentId) =>
        "bmb-comment"u8.ToArray()
            .Concat(articleId.ToByteArray())
            .Concat(commentId.ToByteArray()).ToArray();

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
