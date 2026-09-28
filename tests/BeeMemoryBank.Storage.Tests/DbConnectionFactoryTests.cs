using System;
using System.IO;
using System.Threading.Tasks;
using BeeMemoryBank.Storage.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The connection lifetime bookkeeping of <see cref="DbConnectionFactory"/>: what the drain gate
/// counts, and — the point of review l-root5 #1 — that a connection which fails to initialize is
/// not left counted. A leaked count never heals: the next blind seed waits the whole drain timeout
/// for a connection nobody holds and then fails with 400.
/// </summary>
public sealed class DbConnectionFactoryTests : IDisposable
{
    private readonly string _dir;

    public DbConnectionFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bmb_dbfactory_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private string DbPath => Path.Combine(_dir, "beememorybank.db");

    [Fact]
    public void AConnectionThatOpens_IsCounted_UntilItIsDisposed()
    {
        using var factory = new DbConnectionFactory(_dir);

        var connection = factory.CreateConnection();
        factory.OpenConnections.Should().Be(1);

        connection.Dispose();
        factory.OpenConnections.Should().Be(0);
    }

    [Fact]
    public void AConnectionWhoseInitializationFails_IsNotLeftCounted()
    {
        // A file SQLite can open but cannot read: `sqlite3_open_v2` is lazy, so Open() succeeds and
        // the failure lands in the PRAGMA that runs after it — exactly the shape that used to leak
        // the lease. The assertion is on the count, not on the exception type, because the count is
        // the thing the drain gate reads.
        File.WriteAllText(DbPath, new string('x', 8192));
        using var factory = new DbConnectionFactory(_dir);

        var act = () => factory.CreateConnection();

        act.Should().Throw<SqliteException>("the file is not a database");
        factory.OpenConnections.Should().Be(0,
            "a connection that never reached the caller must not hold the factory's lease");
    }

    [Fact]
    public async Task AfterAFailedInitialization_TheDrainGateStillOpens()
    {
        // The production symptom, not the counter: a held-open lease makes this wait the full
        // timeout and throw, and every later seed then fails with 400.
        File.WriteAllText(DbPath, new string('x', 8192));
        using var factory = new DbConnectionFactory(_dir);
        try { factory.CreateConnection(); } catch (SqliteException) { }

        using var quiesced = factory.BeginQuiesce();
        var wait = () => factory.WaitDrainedAsync(TimeSpan.FromSeconds(5));

        await wait.Should().NotThrowAsync();
    }

    [Fact]
    public void AConnectionThatOpensAfterAFailure_IsStillUsable()
    {
        File.WriteAllText(DbPath, new string('x', 8192));
        using var factory = new DbConnectionFactory(_dir);
        try { factory.CreateConnection(); } catch (SqliteException) { }

        // The real database is put in place - the failed attempt left nothing behind. The pool is
        // cleared first: it still holds a handle to the file that was just unlinked, and reusing
        // that handle would keep reading the old inode rather than the new file.
        File.Delete(DbPath);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={DbPath}"));

        using (var connection = factory.CreateConnection())
        {
            factory.OpenConnections.Should().Be(1);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            cmd.ExecuteScalar().Should().Be("wal", "the connection this factory hands out still works");
        }

        factory.OpenConnections.Should().Be(0);
    }
}