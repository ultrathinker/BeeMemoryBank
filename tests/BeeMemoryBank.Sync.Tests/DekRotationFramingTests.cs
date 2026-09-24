using System.Security.Cryptography;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// End-to-end check that DEK rotation keeps every row's framing: a legacy v0 article body is still
/// v0 (48-byte wrap, no AAD) afterwards and still reads, a v1 body and its version rows (framed with
/// the ARTICLE id, not the version row's own id) are still v1 and still read.
/// </summary>
public class DekRotationFramingTests : SyncTestFixture
{
    private async Task<byte[]> ReadBodyWrappedDekAsync(Guid articleId)
    {
        using var conn = Factory.CreateConnection();
        return await conn.QuerySingleAsync<byte[]>(
            "SELECT encrypted_dek FROM tbl_article_body WHERE article_id = @id", new { id = articleId });
    }

    [Fact]
    public async Task Rotation_KeepsV0RowsV0_AndV1RowsV1_AndEverythingStillReads()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var legacy = await ArticleService.CreateAsync("Legacy", "/", [], "placeholder");
        var modern = await ArticleService.CreateAsync("Modern", "/", [], "modern v1");
        await ArticleService.UpdateAsync(modern.Id, plaintext: "modern v2"); // version 1 = "modern v1"

        var oldDek = Session.GetMasterDek();

        // Turn the first article's body into a row exactly as a pre-v1 build wrote it.
        var entityDek = DekManager.GenerateArticleDek();
        var (v0Ct, v0Iv) = ArticleEncryptor.Encrypt("legacy v0 body", entityDek, aad: null);
        var (v0Enc, v0DekIv) = DekManager.WrapDekLegacyV0(entityDek, oldDek);
        Array.Clear(entityDek);
        await new ArticleBodyRepository(Factory).UpsertAsync(new EncryptedArticleBody
        {
            ArticleId = legacy.Id,
            Ciphertext = v0Ct,
            IV = v0Iv,
            EncryptedDek = v0Enc,
            DekIV = v0DekIv
        });
        (await ArticleService.GetContentAsync(legacy.Id)).Should().Be("legacy v0 body");

        var newDek = RandomNumberGenerator.GetBytes(32);
        var (_, _, tally) = await DekRewrapper.RewrapAllAsync(
            Factory, Session,
            oldDek: oldDek, newDek: (byte[])newDek.Clone(),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(),
            isInitiator: false);

        tally.Unreadable.Should().Be(0);

        var legacyWrapped = await ReadBodyWrappedDekAsync(legacy.Id);
        legacyWrapped.Length.Should().Be(48, "a v0 row must still look v0 to every reader after rotation");
        EnvelopeFraming.IsVersioned(legacyWrapped).Should().BeFalse();

        EnvelopeFraming.IsVersioned(await ReadBodyWrappedDekAsync(modern.Id)).Should().BeTrue();

        // Both bodies read through the normal path, now on the new key.
        (await ArticleService.GetContentAsync(legacy.Id)).Should().Be("legacy v0 body");
        (await ArticleService.GetContentAsync(modern.Id)).Should().Be("modern v2");

        // The version row was re-wrapped under the new key with the ARTICLE-id AAD.
        var versionRepo = new ArticleVersionRepository(Factory, new CallerScopeHolder());
        var version = (await versionRepo.GetAsync(modern.Id, 1))!;
        EnvelopeFraming.IsVersioned(version.EncryptedDek).Should().BeTrue();
        var versionDek = EnvelopeFraming.Article.UnwrapDek(modern.Id, version.EncryptedDek, version.DekIV, newDek);
        EnvelopeFraming.Article.DecryptBodyText(modern.Id, version.EncryptedDek, versionDek, version.Ciphertext, version.IV)
            .Should().Be("modern v1");
        ArticleService.DecryptVersionContent(version).Should().Be("modern v1");
    }
}
