using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// BMB-304 fix2 (P3): the verification of an encrypted copy fails CLOSED. A table that cannot be counted, or a search that cannot run, on both copies used to compare
/// equal (-1 and -1, "failed" and "failed"), so a copy whose search index nobody could read was marked as verified.
/// </summary>
public sealed class SealVerifierFailClosedTests
{
    private static IEnumerable<long> Ids(params long[] ids) => ids;

    private static IEnumerable<long> Throws()
    {
        yield return 1;
        throw new SqliteException("no such table: fts_article", 1);
    }

    private static IEnumerable<long> ThrowsAtOnce()
    {
        throw new SqliteException("no such table: fts_article", 1);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public void TheSameIdsInTheSameOrder_AreTheSame() => SealVerifier.SameOrderedIds(Ids(1, 5, 9), Ids(1, 5, 9)).Should().BeTrue();

    [Fact]
    public void AnEmptyAnswerOnBothSides_IsTheSame() => SealVerifier.SameOrderedIds(Ids(), Ids()).Should().BeTrue("a common word nobody uses finds nothing in either copy");

    [Theory]
    [InlineData(new long[] { 1, 5, 9 }, new long[] { 1, 5 })]
    [InlineData(new long[] { 1, 5 }, new long[] { 1, 5, 9 })]
    [InlineData(new long[] { 1, 5, 9 }, new long[] { 1, 9, 5 })]
    public void ADifferentAnswer_IsADifference(long[] source, long[] target) => SealVerifier.SameOrderedIds(source, target).Should().BeFalse();

    [Fact]
    public void BothReadersThatCannotRun_AreNotTheSame() => SealVerifier.SameOrderedIds(ThrowsAtOnce(), ThrowsAtOnce()).Should().BeFalse("two indexes nobody could read do not agree");

    [Fact]
    public void BothReadersThatFailAfterARow_AreNotTheSame() => SealVerifier.SameOrderedIds(Throws(), Throws()).Should().BeFalse();

    [Fact]
    public void OneReaderThatCannotRun_IsADifference()
    {
        SealVerifier.SameOrderedIds(ThrowsAtOnce(), Ids(1)).Should().BeFalse();
        SealVerifier.SameOrderedIds(Ids(1), ThrowsAtOnce()).Should().BeFalse();
    }

    private static SqliteConnection Open()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        return c;
    }

    [Fact]
    public void ASearchThatCannotRunOnEitherCopy_FailsTheVerification()
    {
        using var source = Open();
        using var target = Open();   // neither holds fts_article: the query fails on both

        var act = () => SealVerifier.Search(source, target, ["Quarterly report"]);

        act.Should().Throw<SealMigrationException>().Which.Code.Should().Be(SealFailures.VerifyTree);
    }

    [Fact]
    public void ASearchThatRunsAndAgrees_PassesAndOneThatDiffers_Fails()
    {
        using var source = Open();
        using var target = Open();
        foreach (var c in new[] { source, target })
            c.Execute("CREATE VIRTUAL TABLE fts_article USING fts5(title)");
        source.Execute("INSERT INTO fts_article(rowid, title) VALUES (1, 'Quarterly report'), (2, 'Other')");
        target.Execute("INSERT INTO fts_article(rowid, title) VALUES (1, 'Quarterly report'), (2, 'Other')");

        SealVerifier.Search(source, target, ["Quarterly report"]);   // the common word: both copies answer the same, nothing is thrown

        target.Execute("INSERT INTO fts_article(rowid, title) VALUES (3, 'Quarterly numbers')");
        var act = () => SealVerifier.Search(source, target, ["Quarterly report"]);
        act.Should().Throw<SealMigrationException>().Which.Code.Should().Be(SealFailures.VerifyTree);
    }

    [Fact]
    public void ATableThatCannotBeCounted_FailsTheVerificationInsteadOfCountingMinusOne()
    {
        using var connection = Open();
        connection.Execute("CREATE TABLE tbl_a (x INTEGER); INSERT INTO tbl_a VALUES (1), (2)");

        SealVerifier.RowCount(connection, "tbl_a", "main").Should().Be(2);
        var act = () => SealVerifier.RowCount(connection, "tbl_missing", "main");

        var failure = act.Should().Throw<SealMigrationException>().Which;
        failure.Code.Should().Be(SealFailures.VerifyCounts);
        failure.Message.Should().Contain("could not be counted").And.NotContain("-1");
    }
}
