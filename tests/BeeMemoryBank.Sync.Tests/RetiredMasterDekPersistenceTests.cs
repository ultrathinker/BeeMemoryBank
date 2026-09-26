using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A body sealed under a master DEK that a rotation has retired can still arrive afterwards — from a
/// peer that was offline during the rotation or had not applied it yet (a phone never does). It used
/// to open only while the retired key sat in memory: after a restart the hub answered 500 for such
/// articles, for good (BMB-31, scenario 20). The rotation now keeps the retired key in the database,
/// sealed under the new one, and every unlock loads it back.
/// </summary>
public class RetiredMasterDekPersistenceTests : SyncTestFixture
{
    [Fact]
    public async Task Body_under_a_retired_key_opens_after_a_restart_and_after_a_second_rotation()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);

        var article = await ArticleService.CreateAsync("late", "/", [], "body under the first key");
        var lateBody = (await BodyRepo.GetByArticleIdAsync(article.Id))!;   // what a late peer would send

        var dek1 = Session.GetMasterDek();
        var dek2 = RandomNumberGenerator.GetBytes(32);
        await DekRewrapper.RewrapAllAsync(Factory, Session, dek1, (byte[])dek2.Clone(),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        // The late body lands after the rotation, still sealed under the first key.
        using (var conn = Factory.CreateConnection())
            await conn.ExecuteAsync(
                "UPDATE tbl_article_body SET encrypted_dek = @enc, dek_iv = @iv WHERE article_id = @id",
                new { enc = lateBody.EncryptedDek, iv = lateBody.DekIV, id = article.Id });

        OpensAfterRestart(article.Id, lateBody, dek2).Should().BeTrue(
            "the retired key is stored under the current one and loaded at unlock");

        // A second rotation must carry the first retired key forward.
        var dek3 = RandomNumberGenerator.GetBytes(32);
        await DekRewrapper.RewrapAllAsync(Factory, Session, (byte[])dek2.Clone(), (byte[])dek3.Clone(),
            newEpoch: 3, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        OpensAfterRestart(article.Id, lateBody, dek3).Should().BeTrue(
            "the next rotation re-wraps stored retired keys like every other node data key");
    }

    [Fact]
    public async Task Without_the_store_a_fresh_session_cannot_open_it()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var article = await ArticleService.CreateAsync("late", "/", [], "body under the first key");
        var lateBody = (await BodyRepo.GetByArticleIdAsync(article.Id))!;
        var dek2 = RandomNumberGenerator.GetBytes(32);
        await DekRewrapper.RewrapAllAsync(Factory, Session, Session.GetMasterDek(), (byte[])dek2.Clone(),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        var bare = new SessionService(new KeySlotRepository(Factory));
        bare.UnlockWithDek((byte[])dek2.Clone());
        var open = () => bare.TryUnwrapWithCandidates(dek =>
            EnvelopeFraming.Article.UnwrapDek(article.Id, lateBody.EncryptedDek, lateBody.DekIV, dek));
        open.Should().Throw<Exception>("the control: only the stored retired key makes the difference");
    }

    private bool OpensAfterRestart(Guid articleId, Core.Models.EncryptedArticleBody body, byte[] currentDek)
    {
        // A fresh SessionService is what a restarted process has: no in-memory retired cache.
        var restarted = new SessionService(new KeySlotRepository(Factory), null, new RetiredMasterDekStore(Factory));
        restarted.UnlockWithDek((byte[])currentDek.Clone());
        try
        {
            var dek = restarted.TryUnwrapWithCandidates(k =>
                EnvelopeFraming.Article.UnwrapDek(articleId, body.EncryptedDek, body.DekIV, k));
            Array.Clear(dek);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
