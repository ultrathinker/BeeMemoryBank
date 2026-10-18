using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The protocol a node declares comes from the activation row in its own database. A read of it that FAILS (a lock, a database that is still opening)
/// says nothing, and must not be remembered as "not activated" for the next few seconds: a node that holds an activation never goes on writing clear
/// content (found by the first command after <c>bmb init</c>, which now sets a node up with its activation, BMB-306).
/// </summary>
public sealed class SyncProtocolPolicyReadTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "bmb_policy_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { }
    }

    private void MakeTable(bool withActivation)
    {
        using var conn = new SqliteConnection($"Data Source={_file};Pooling=False");
        conn.Open();
        conn.Execute("CREATE TABLE tbl_metadata_activation (activation_id TEXT PRIMARY KEY)");
        if (withActivation) conn.Execute("INSERT INTO tbl_metadata_activation VALUES ('a')");
    }

    private sealed class Factory(string file, int failures) : IDbConnectionFactory
    {
        private int _failuresLeft = failures;
        public int Calls { get; private set; }
        public string DatabaseId => file;

        public IDbConnection CreateConnection()
        {
            Calls++;
            if (_failuresLeft-- > 0) throw new SqliteException("database is locked", 5);
            var conn = new SqliteConnection($"Data Source={file};Pooling=False");
            conn.Open();
            return conn;
        }
    }

    [Fact]
    public void A_read_that_failed_is_asked_again_at_once_and_the_activation_is_seen()
    {
        MakeTable(withActivation: true);
        var factory = new Factory(_file, failures: 1);
        var policy = new SyncProtocolPolicy(factory);

        policy.SealsMetadata.Should().BeFalse("the first read failed: nothing is known yet");
        policy.SealsMetadata.Should().BeTrue("the second call reads again instead of answering no for the next seconds");
        policy.Declared.Should().Be(EventClasses.SealedProtocol);
        factory.Calls.Should().Be(2);
    }

    [Fact]
    public void A_read_that_worked_and_found_nothing_is_not_repeated_on_every_call()
    {
        MakeTable(withActivation: false);
        var factory = new Factory(_file, failures: 0);
        var policy = new SyncProtocolPolicy(factory);

        policy.SealsMetadata.Should().BeFalse();
        policy.SealsMetadata.Should().BeFalse();
        policy.SealsMetadata.Should().BeFalse();

        factory.Calls.Should().Be(1, "a worked read is remembered for a few seconds, as before");
    }

    [Fact]
    public void A_database_without_the_table_is_not_activated()
    {
        File.WriteAllBytes(_file, []);
        var policy = new SyncProtocolPolicy(new Factory(_file, failures: 0));

        policy.SealsMetadata.Should().BeFalse("nothing can have been activated where there is no table to record it");
    }
}
