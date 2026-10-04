using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages comments with E2E encryption using the parent article's DEK.
/// <para>
/// A comment on a protected article carries a second layer: its text is sealed under the article's
/// passphrase (<see cref="ProtectedContentCodec"/>, the same codec as the body) before the article
/// DEK seals it. Without that, anyone who could open the vault read the comments of an article whose
/// body they could not (BMB-36). Writing one needs the passphrase; reading one without it yields
/// <see cref="LockedText"/>. Protecting, unprotecting or changing the passphrase re-seals the
/// article's comments through <see cref="ReprotectAsync"/>.
/// </para>
/// </summary>
public class CommentService(
    ICommentRepository commentRepo,
    IArticleBodyRepository bodyRepo,
    SessionService session,
    IEventLogger eventLogger,
    IArticleRepository? articleRepo = null,
    IEventLogRepository? eventLogRepo = null)
{
    /// <summary>What a passphrase-sealed comment reads as without the article's passphrase.</summary>
    public const string LockedText = "\U0001F512 Protected comment — unlock the article to read it.";

    /// <summary>
    /// Creates an encrypted comment. On a protected article <paramref name="passphrase"/> is required
    /// (callers pass one they already verified: the API's unlock cache, the phone's unlock holder) and
    /// the text is sealed under it too; <see cref="ArticleLockedException"/> otherwise.
    /// <paramref name="createdAt"/> keeps a re-sealed comment's original time.
    /// </summary>
    public async Task<Comment> CreateAsync(Guid articleId, string plaintext, string? passphrase = null, DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new ArgumentException("Text is required");

        if (await IsProtectedAsync(articleId))
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new ArticleLockedException("Unlock the article to comment on it.");
            plaintext = ProtectedContentCodec.Wrap(plaintext, passphrase);
        }

        return await CreateSealedAsync(articleId, plaintext, createdAt);
    }

    private async Task<Comment> CreateSealedAsync(Guid articleId, string text, DateTime? createdAt)
    {
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
            (ciphertext, iv) = ArticleEncryptor.Encrypt(text, articleDek, CommentAad(articleId, commentId));
        }
        finally
        {
            Array.Clear(articleDek);
        }

        var comment = await commentRepo.CreateEncryptedAsync(articleId, commentId, ciphertext, iv, createdAt: createdAt);
        await eventLogger.LogCommentCreateAsync(comment);
        return comment;
    }

    private async Task<bool> IsProtectedAsync(Guid articleId) =>
        articleRepo != null && (await articleRepo.GetByIdAsync(articleId))?.Protected == true;

    /// <summary>
    /// Decrypts and returns comment text. A passphrase-sealed comment is opened with
    /// <paramref name="passphrase"/>; without it (or with a wrong one) the result is <see cref="LockedText"/>.
    /// </summary>
    public async Task<string> DecryptTextAsync(Comment comment, string? passphrase = null) =>
        (await OpenAsync(comment, passphrase)).Text;

    /// <summary>Text of a comment and whether it stayed sealed under a passphrase the caller lacks.</summary>
    private async Task<(string Text, bool Locked)> OpenAsync(Comment comment, string? passphrase)
    {
        var text = await DecryptOuterAsync(comment);
        if (!ProtectedContentCodec.IsProtected(text))
            return (text, false);
        if (string.IsNullOrEmpty(passphrase))
            return (LockedText, true);
        try
        {
            return (ProtectedContentCodec.Unwrap(text, passphrase), false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (LockedText, true);
        }
    }

    private async Task<string> DecryptOuterAsync(Comment comment)
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

    /// <summary>Gets comments for an article, decrypting encrypted ones (see <see cref="DecryptTextAsync"/>).</summary>
    public async Task<List<(Comment comment, string text)>> GetDecryptedByArticleAsync(Guid articleId, string? passphrase = null) =>
        (await GetOpenedByArticleAsync(articleId, passphrase)).Select(c => (c.Comment, c.Text)).ToList();

    /// <summary>Like <see cref="GetDecryptedByArticleAsync"/>, also saying which comments stayed locked.</summary>
    public async Task<List<(Comment Comment, string Text, bool Locked)>> GetOpenedByArticleAsync(Guid articleId, string? passphrase = null)
    {
        var comments = await commentRepo.GetByArticleIdAsync(articleId);
        var result = new List<(Comment, string, bool)>();
        foreach (var c in comments)
        {
            var (text, locked) = await OpenAsync(c, passphrase);
            result.Add((c, text, locked));
        }
        return result;
    }

    /// <summary>
    /// Re-seals every comment of an article after its protection changed: opened with
    /// <paramref name="oldPassphrase"/> (null: they were not passphrase-sealed), sealed again under
    /// <paramref name="newPassphrase"/> (null: plain again). Comment rows are immutable over sync, so
    /// each one is deleted and created anew with the same text and time; peers get both events.
    /// A comment that does not open with the old passphrase is left alone. Returns how many moved.
    /// </summary>
    public async Task<int> ReprotectAsync(Guid articleId, string? oldPassphrase, string? newPassphrase)
    {
        var moved = 0;
        foreach (var c in await commentRepo.GetByArticleIdAsync(articleId))
        {
            var outer = await DecryptOuterAsync(c);
            string text;
            if (ProtectedContentCodec.IsProtected(outer))
            {
                if (string.IsNullOrEmpty(oldPassphrase)) continue;
                try { text = ProtectedContentCodec.Unwrap(outer, oldPassphrase); }
                catch (System.Security.Cryptography.CryptographicException) { continue; }
            }
            else
            {
                text = outer;
            }

            var sealedText = string.IsNullOrEmpty(newPassphrase) ? text : ProtectedContentCodec.Wrap(text, newPassphrase);
            await CreateSealedAsync(articleId, sealedText, c.CreatedAt);
            await DeleteAsync(c.Id);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Seals every comment still stored as plaintext (written by an Android build that skipped the
    /// encryption, or by a very old version) and removes plaintext comment events from this node's
    /// event log. Each such comment is re-created encrypted with its original text and time — peers
    /// had refused the plaintext one, so this is also the first copy they can accept. The plaintext
    /// events are deleted rather than kept: every peer refuses them anyway, and they are the only
    /// other place the text sits in clear. Idempotent; run after every unlock.
    /// </summary>
    public async Task<int> SealLegacyPlaintextAsync()
    {
        var sealedCount = 0;
        foreach (var c in await commentRepo.GetPlaintextAsync())
        {
            if (string.IsNullOrEmpty(c.Text)) continue;
            // On a protected article this seals it under the article key only (no passphrase is at
            // hand here) — still a step up from plaintext; re-protecting the article adds the rest.
            await CreateSealedAsync(c.ArticleId, c.Text, c.CreatedAt);
            await DeleteAsync(c.Id);
            sealedCount++;
        }

        if (eventLogRepo != null)
            await eventLogRepo.DeletePlaintextCommentEventsAsync();
        return sealedCount;
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

/// <summary>Thrown when writing to a protected article that the caller has not unlocked.</summary>
public sealed class ArticleLockedException(string message) : InvalidOperationException(message);
