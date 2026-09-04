using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.DekRotation;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A DEK rotation must not be destroyed by a single row it cannot open.
///
/// <para>
/// The rewrap walks every DEK-bearing row and unwraps it with the OLD master key. A peer that
/// applied the rotation before this node did ships articles whose body DEK is already wrapped under
/// the NEW key — and with the default <c>auto_accept = false</c>, that is the expected outcome of
/// any rotation where somebody wrote in the window between propose and accept, not a corner case.
/// </para>
///
/// <para>
/// Such a row used to throw <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/>
/// straight out of the loop, roll the whole rotation transaction back, and do it again on every
/// retry. <c>SwapMasterDek</c> was never reached, so the node could never finish the rotation and
/// the only way out was to wipe it and re-join. Rolling back protected nothing: the rows that could
/// have been rotated stayed unrotated too.
/// </para>
/// </summary>
public class DekRotationRaceTests : SyncTestFixture
{
    /// <summary>The AAD an article body's DEK is sealed under — see ArticleService.CreateAsync.</summary>
    private static byte[] BodyDekAad(Guid articleId) =>
        "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();

    /// <summary>
    /// Re-seals one article's body DEK under <paramref name="newDek"/>, which is exactly what a peer
    /// that rotated first would have shipped us.
    /// </summary>
    private async Task PutBodyOnNewKeyAsync(Guid articleId, byte[] oldDek, byte[] newDek)
    {
        using var conn = Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<dynamic>(
            "SELECT encrypted_dek AS enc, dek_iv AS iv FROM tbl_article_body WHERE article_id = @id",
            new { id = articleId });

        var aad = BodyDekAad(articleId);
        var plain = DekManager.UnwrapDek((byte[])row.enc, (byte[])row.iv, oldDek, aad);
        var (newEnc, newIv) = DekManager.WrapDek(plain, newDek, aad);
        Array.Clear(plain);

        await conn.ExecuteAsync(
            "UPDATE tbl_article_body SET encrypted_dek = @enc, dek_iv = @iv WHERE article_id = @id",
            new { enc = newEnc, iv = newIv, id = articleId });
    }

    [Fact]
    public async Task RowAlreadyOnTheNewKey_DoesNotAbortTheRotation_AndBothArticlesStayReadable()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var normal = await ArticleService.CreateAsync("Normal", "/", [], "written before the rotation");
        var raced = await ArticleService.CreateAsync("Raced", "/", [], "arrived from a peer that rotated first");

        // Snapshot the current master DEK before anything swaps it; mint the DEK the rotation moves to.
        var oldDek = Session.GetMasterDek();
        var newDek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        await PutBodyOnNewKeyAsync(raced.Id, oldDek, newDek);

        var (_, _, tally) = await DekRewrapper.RewrapAllAsync(
            Factory, Session,
            oldDek: Session.GetMasterDek(), newDek: newDek,
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(),
            isInitiator: false);

        tally.Rewrapped.Should().Be(1, "the article written before the rotation is the one that needed re-sealing");
        tally.AlreadyOnNewKey.Should().Be(1, "the article that raced ahead was already where it needed to be");
        tally.Unreadable.Should().Be(0);

        // The point of the whole exercise: the rotation completed, so the session is on the new key
        // and BOTH articles open. Before the fix this line was never reached — the rotation threw.
        (await ArticleService.GetContentAsync(normal.Id)).Should().Be("written before the rotation");
        (await ArticleService.GetContentAsync(raced.Id)).Should().Be("arrived from a peer that rotated first");
    }

    /// <summary>
    /// A row that opens under neither key is genuinely unrecoverable here, but it must not take the
    /// node with it: the rotation still completes, every other row still rotates, and the operator
    /// is told which row to chase rather than finding out when a user opens it months later.
    /// </summary>
    [Fact]
    public async Task RowReadableUnderNeitherKey_IsReportedButDoesNotAbortTheRotation()
    {
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        var good = await ArticleService.CreateAsync("Good", "/", [], "this one is fine");
        var broken = await ArticleService.CreateAsync("Broken", "/", [], "this one is not");

        var oldDek = Session.GetMasterDek();
        var newDek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        // Corrupt one row's wrapped DEK so it authenticates under nothing at all.
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_article_body SET encrypted_dek = @enc WHERE article_id = @id",
                new { enc = System.Security.Cryptography.RandomNumberGenerator.GetBytes(60), id = broken.Id });
        }

        var (_, _, tally) = await DekRewrapper.RewrapAllAsync(
            Factory, Session,
            oldDek: oldDek, newDek: newDek,
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(),
            isInitiator: false);

        tally.Unreadable.Should().Be(1);
        tally.UnreadableExamples.Should().ContainSingle()
            // Guids are stored as uppercase TEXT, so compare without regard to case — the point is
            // that the row is NAMED, not what casing the storage layer happens to use.
            .Which.Should().ContainEquivalentOf(broken.Id.ToString(), "the operator needs the row named, not just counted");
        tally.Rewrapped.Should().Be(1, "the healthy article must still have been rotated");

        (await ArticleService.GetContentAsync(good.Id)).Should().Be("this one is fine");
    }
}
