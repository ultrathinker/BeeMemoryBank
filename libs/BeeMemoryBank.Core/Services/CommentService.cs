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
            // Always the current comment framing (with AAD), whatever the parent body's framing is.
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

        var ciphertext = comment.Ciphertext;
        var iv = comment.IV;
        // Whole attempt per candidate master DEK, in the session's candidate order (current, then
        // retired): unwrap the article DEK, then open the comment with it.
        return session.TryUnwrapWithCandidates(masterDek =>
        {
            var articleDek = EnvelopeFraming.Article.UnwrapDek(comment.ArticleId, body.EncryptedDek, body.DekIV, masterDek);
            try
            {
                return DecryptCommentText(comment.ArticleId, comment.CommentId, ciphertext, iv, articleDek);
            }
            finally
            {
                Array.Clear(articleDek);
            }
        });
    }

    /// <summary>
    /// Opens one comment ciphertext with its article DEK. A comment row carries no framing marker,
    /// and its framing is independent of the parent body's (a body update re-seals the body as v1
    /// but never touches its comments), so the comment's own framing is detected by trial: the
    /// current framing (<see cref="CommentAad"/>) first, and only on an authentication-tag failure
    /// the legacy framing with no AAD.
    /// <para>
    /// Invariant: the no-AAD fallback accepts only ciphertexts that were sealed without AAD, i.e.
    /// legacy comments. Those carry no article/comment binding in the first place, so the fallback
    /// grants an attacker with DB write access nothing beyond what those rows already allowed;
    /// every comment written now is sealed with AAD and cannot be opened by the fallback under a
    /// different article or comment id.
    /// </para>
    /// </summary>
    private static string DecryptCommentText(Guid articleId, Guid commentId, byte[] ciphertext, byte[] iv, byte[] articleDek)
    {
        try
        {
            return ArticleEncryptor.Decrypt(ciphertext, iv, articleDek, CommentAad(articleId, commentId));
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            return ArticleEncryptor.Decrypt(ciphertext, iv, articleDek, aad: null);
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
