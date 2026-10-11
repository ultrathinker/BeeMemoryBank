using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary><see cref="SealedSqlite.OpenKeyed"/> (docs/design/step-b-spec.md 7.1): the keyed open of one file, and the read that leaves a directory as it found it.</summary>
public sealed class SealedSqliteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_sealedsqlite_" + Guid.NewGuid().ToString("N"));

    // No ClearAllPools: the pool is process-wide and other tests of this assembly run beside this one; every connection here is not pooled.
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string[] Listing() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public async Task An_immutable_read_of_a_sealed_WAL_file_creates_no_file_and_changes_no_byte()
    {
        Directory.CreateDirectory(_dir);
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 40);
        using (var probe = SealedSqlite.OpenKeyed(db.Path, db.Key))
            probe.ExecuteScalar<string>("PRAGMA journal_mode").Should().Be("wal", "the fixture is a WAL-mode vault, like every node's");
        var before = (Listing(), File.ReadAllBytes(db.Path));
        before.Item1.Should().Equal("vault.db");

        using (var conn = SealedSqlite.OpenKeyed(db.Path, db.Key, readOnly: true, immutable: true))
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().Be(40);

        Listing().Should().Equal(before.Item1, "no -wal, no -shm, no journal");
        File.ReadAllBytes(db.Path).Should().Equal(before.Item2);
    }

    [Fact]
    public async Task A_wrong_key_is_refused_for_an_immutable_open_too()
    {
        Directory.CreateDirectory(_dir);
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 5);

        var act = () => SealedSqlite.OpenKeyed(db.Path, new InMemoryVaultKeySource().GetPageKey(DbPurposes.Vault), readOnly: true, immutable: true);

        act.Should().Throw<VaultKeyMismatchException>();
        Listing().Should().Equal("vault.db");
    }

    [Fact]
    public async Task A_plain_read_only_open_of_a_sealed_WAL_file_leaves_its_sidecars_which_is_why_the_re_key_reads_immutable()
    {
        Directory.CreateDirectory(_dir);
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 40);

        using (var conn = SealedSqlite.OpenKeyed(db.Path, db.Key, readOnly: true))
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().Be(40);

        Listing().Should().Equal("vault.db", "vault.db-shm", "vault.db-wal");
    }
}
