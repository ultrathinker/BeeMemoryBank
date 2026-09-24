using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// A comment's framing is its own: it is detected by trial (with comment AAD, then legacy no-AAD),
/// never inferred from the parent article body's framing, which changes when the body is edited
/// while the comments stay as they were written.
/// </summary>
public class CommentFramingTests : TestFixture
{
    private CommentRepository _commentRepo = null!;
    private ArticleBodyRepository _bodyRepo = null!;
    private CommentService _comments = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _commentRepo = new CommentRepository(Factory, ScopeHolder);
        _bodyRepo = new ArticleBodyRepository(Factory);
        _comments = new CommentService(_commentRepo, _bodyRepo, Session, new NullEventLogger());
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    /// <summary>
    /// Creates an article and replaces its body with a legacy v0 row (48-byte DEK wrap, no AAD).
    /// Returns the article id and its plaintext DEK (caller clears it).
    /// </summary>
    private async Task<(Guid articleId, byte[] articleDek)> CreateV0ArticleAsync(string text)
    {
        var article = await ArticleService.CreateAsync("Legacy", "/", [], "placeholder");
        var articleDek = DekManager.GenerateArticleDek();
        var master = Session.GetMasterDek();
        try
        {
            var (ct, iv) = ArticleEncryptor.Encrypt(text, articleDek, aad: null);
            var (enc, dekIv) = DekManager.WrapDekLegacyV0(articleDek, master);
            await _bodyRepo.UpsertAsync(new EncryptedArticleBody
            {
                ArticleId = article.Id, Ciphertext = ct, IV = iv, EncryptedDek = enc, DekIV = dekIv
            });
        }
        finally
        {
            Array.Clear(master);
        }
        EnvelopeFraming.IsVersioned((await _bodyRepo.GetByArticleIdAsync(article.Id))!.EncryptedDek).Should().BeFalse();
        return (article.Id, articleDek);
    }

    private async Task<Comment> CommentByIdAsync(Guid articleId, Guid commentId) =>
        (await _commentRepo.GetByArticleIdAsync(articleId)).Single(c => c.CommentId == commentId);

    [Fact]
    public async Task CommentOnAV0Article_IsReadable()
    {
        var (articleId, articleDek) = await CreateV0ArticleAsync("v0 body");
        Array.Clear(articleDek);

        var comment = await _comments.CreateAsync(articleId, "comment on a v0 article");

        (await _comments.DecryptTextAsync(comment)).Should().Be("comment on a v0 article");
    }

    [Fact]
    public async Task LegacyNoAadComment_StaysReadable_AfterTheParentIsEditedFromV0ToV1()
    {
        var (articleId, articleDek) = await CreateV0ArticleAsync("v0 body");
        var commentId = Guid.NewGuid();
        byte[] ct, iv;
        try
        {
            // A comment exactly as the legacy framing wrote it: article DEK, no AAD.
            (ct, iv) = ArticleEncryptor.Encrypt("legacy comment", articleDek, aad: null);
        }
        finally
        {
            Array.Clear(articleDek);
        }
        await _commentRepo.CreateEncryptedAsync(articleId, commentId, ct, iv);

        (await _comments.DecryptTextAsync(await CommentByIdAsync(articleId, commentId))).Should().Be("legacy comment");

        // Editing the body re-seals it as v1 (same article DEK) but leaves the comment untouched.
        await ArticleService.UpdateAsync(articleId, plaintext: "v1 body now");
        EnvelopeFraming.IsVersioned((await _bodyRepo.GetByArticleIdAsync(articleId))!.EncryptedDek).Should().BeTrue();

        (await _comments.DecryptTextAsync(await CommentByIdAsync(articleId, commentId))).Should().Be("legacy comment");
    }

    [Fact]
    public async Task CommentWrittenUnderARetiredDek_IsReadableAfterSwapMasterDek()
    {
        var article = await ArticleService.CreateAsync("Rotated", "/", [], "body");
        var comment = await _comments.CreateAsync(article.Id, "written before the swap");

        Session.SwapMasterDek(RandomNumberGenerator.GetBytes(32));

        (await _comments.DecryptTextAsync(await CommentByIdAsync(article.Id, comment.CommentId)))
            .Should().Be("written before the swap");
    }

    [Fact]
    public async Task CurrentFramingComment_DoesNotOpenUnderAnotherCommentId()
    {
        // The no-AAD fallback must not turn an AAD-bound comment into a movable one.
        var article = await ArticleService.CreateAsync("Bound", "/", [], "body");
        var comment = await _comments.CreateAsync(article.Id, "bound to its id");

        var moved = new Comment
        {
            ArticleId = comment.ArticleId,
            CommentId = Guid.NewGuid(),
            Ciphertext = comment.Ciphertext,
            IV = comment.IV,
            Encrypted = true
        };

        var act = async () => await _comments.DecryptTextAsync(moved);
        await act.Should().ThrowAsync<CryptographicException>();
    }
}
