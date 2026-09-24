using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Right after <see cref="SessionService.SwapMasterDek"/> (a DEK rotation, or a peer event that
/// raced one), rows can still be wrapped under the now-retired master DEK. Every path that opens an
/// EXISTING row must then behave the same: if the article reads, it must also update, show its
/// versions, accept comments and be found by content search. New writes use the current key.
/// </summary>
public class RetiredMasterDekTests : TestFixture
{
    private ArticleVersionRepository _versionRepo = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _versionRepo = new ArticleVersionRepository(Factory, ScopeHolder);
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    /// <summary>Retires the current master DEK: every row written so far is now on a retired key.</summary>
    private byte[] RotateInMemory()
    {
        var newDek = RandomNumberGenerator.GetBytes(32);
        Session.SwapMasterDek((byte[])newDek.Clone());
        return newDek;
    }

    private async Task<(byte[] enc, byte[] iv)> ReadBodyDekAsync(Guid articleId)
    {
        using var conn = Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<dynamic>(
            "SELECT encrypted_dek AS enc, dek_iv AS iv FROM tbl_article_body WHERE article_id = @id",
            new { id = articleId });
        return ((byte[])row.enc, (byte[])row.iv);
    }

    [Fact]
    public async Task ArticleOnRetiredDek_CanBeUpdated_AndTheNewBodyIsOnTheCurrentKey()
    {
        var article = await ArticleService.CreateAsync("Rotated", "/", [], "before rotation");
        var currentDek = RotateInMemory();

        // Reads already worked through the candidate walk; the point is that writes now do too.
        (await ArticleService.GetContentAsync(article.Id)).Should().Be("before rotation");

        await ArticleService.UpdateAsync(article.Id, plaintext: "after rotation");

        (await ArticleService.GetContentAsync(article.Id)).Should().Be("after rotation");

        // The re-sealed body is wrapped under the CURRENT master DEK, not the retired one.
        var (enc, iv) = await ReadBodyDekAsync(article.Id);
        var unwrap = () => EnvelopeFraming.Article.UnwrapDek(article.Id, enc, iv, currentDek);
        unwrap.Should().NotThrow();
    }

    [Fact]
    public async Task VersionsOnRetiredDek_AreReadable()
    {
        var article = await ArticleService.CreateAsync("Versioned", "/", [], "v1 text");
        await ArticleService.UpdateAsync(article.Id, plaintext: "v2 text");
        RotateInMemory();

        // Version 1 was snapshotted (with its wrapped DEK) under the now-retired key.
        var v1 = await _versionRepo.GetAsync(article.Id, 1);
        v1.Should().NotBeNull();
        ArticleService.DecryptVersionContent(v1!).Should().Be("v1 text");

        // An update after the rotation snapshots the retired-key body as version 2; it must read too.
        await ArticleService.UpdateAsync(article.Id, plaintext: "v3 text");
        var v2 = await _versionRepo.GetAsync(article.Id, 2);
        ArticleService.DecryptVersionContent(v2!).Should().Be("v2 text");
        (await ArticleService.GetContentAsync(article.Id)).Should().Be("v3 text");
    }

    [Fact]
    public async Task VersionUnderAnUnknownKey_StillFails()
    {
        // Candidates widen the key set to current + retired, not to "anything": a version whose DEK
        // was never wrapped under any key this session holds must still refuse to open.
        var article = await ArticleService.CreateAsync("Foreign", "/", [], "one");
        await ArticleService.UpdateAsync(article.Id, plaintext: "two");
        var v1 = (await _versionRepo.GetAsync(article.Id, 1))!;

        var foreign = EnvelopeFraming.Article.SealNewText(article.Id, "foreign", RandomNumberGenerator.GetBytes(32));
        v1.Ciphertext = foreign.Ciphertext;
        v1.IV = foreign.Iv;
        v1.EncryptedDek = foreign.WrappedDek;
        v1.DekIV = foreign.DekIv;

        var act = () => ArticleService.DecryptVersionContent(v1);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task CommentOnArticleOnRetiredDek_CanBeCreatedAndRead()
    {
        var comments = new CommentService(
            new CommentRepository(Factory, ScopeHolder), new ArticleBodyRepository(Factory), Session, new NullEventLogger());
        var article = await ArticleService.CreateAsync("Commented", "/", [], "body");
        RotateInMemory();

        var comment = await comments.CreateAsync(article.Id, "a comment after rotation");

        (await comments.DecryptTextAsync(comment)).Should().Be("a comment after rotation");
    }

    [Fact]
    public async Task BodyOnRetiredDek_IsFoundByContentSearch()
    {
        var article = await ArticleService.CreateAsync("Needle holder", "/", [], "the zyxwvutsrq marker");
        RotateInMemory();

        var results = await SearchService.SearchWithContentAsync("zyxwvutsrq");

        results.Articles.Select(a => a.Id).Should().Contain(article.Id);
    }
}
