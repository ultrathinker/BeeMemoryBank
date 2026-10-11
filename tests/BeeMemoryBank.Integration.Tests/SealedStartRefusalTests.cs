using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Collects the messages of every logger, so a test can read what a node wrote to its log.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose() { }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines) owner.Lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}

/// <summary>
/// A data folder that cannot start (docs/design/step-b-spec.md 2.5, red test 15.3.18): an encrypted database beside a plain companion is refused by
/// name, nothing is opened, written, swept or served; the node stays up only to say why. Moving the file out lets it start closed, as usual.
/// </summary>
public sealed class SealedStartRefusalTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>, IDisposable
{
    private readonly SealedCorpus.Pool _pool = SealedCorpus.Pool.Create(Path.Combine(Path.GetTempPath(), "bmb_refusal_pool_" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_pool.Dir)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("chat.db")]
    [InlineData("chat.db-wal")]
    [InlineData("beememorybank.db-journal")]
    public async Task A_plain_companion_beside_a_sealed_pair_refuses_the_start_by_name_and_changes_nothing(string companion)
    {
        var files = await SealedApiDirBuilder.BuildAsync(template);
        var source = companion switch
        {
            "chat.db" => _pool.PlainChat,
            "chat.db-wal" => _pool.PlainWal,
            _ => _pool.PlainJournal,
        };
        var sealedChat = File.ReadAllBytes(Path.Combine(files.Dir, "chat.db"));   // what a plain chat.db planted at that name replaces
        File.Copy(source, Path.Combine(files.Dir, companion), overwrite: true);
        var before = Hashes(files.Dir);

        var log = new CapturingLoggerProvider();
        using var refused = SealedApiNode.Over(files, services => services.AddSingleton<ILoggerProvider>(log));
        using var client = refused.Client();

        // Served: the reason, in the status, the start message and the log.
        var status = await SealedApiNode.GetJsonAsync(client, "/api/vault/status");
        status.GetProperty("mode").GetString().Should().Be("broken");
        status.GetProperty("broken_reason").GetString().Should().Be("plain_companion");
        status.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should().Contain(companion);
        status.GetProperty("message").GetString().Should().Contain(companion);
        lock (log.Lines) log.Lines.Should().Contain(l => l.Contains("plain_companion") && l.Contains(companion));

        // Not served: every other route.
        using (var health = await client.GetAsync("/health")) health.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        foreach (var path in new[] { "/api/session/status", "/api/articles/00000000-0000-0000-0000-000000000001", "/api/tree" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, path);
            (await response.Content.ReadAsStringAsync()).Should().Contain("vault_broken", path);
        }
        using (var login = await refused.LoginAsync(client, SealedNodeTemplate.Password))
            login.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "no door is tried on a folder that cannot start");

        // No connection to any file, no write, no sweep, no creation: the folder is byte for byte what it was.
        Hashes(files.Dir).Should().BeEquivalentTo(before);

        // The owner moves the file out of the data folder: the node starts closed as usual and unlocks.
        var away = Path.Combine(Path.GetTempPath(), "bmb_moved_out_" + Guid.NewGuid().ToString("N") + "_" + companion);
        File.Move(Path.Combine(files.Dir, companion), away);
        if (companion == "chat.db") File.WriteAllBytes(Path.Combine(files.Dir, "chat.db"), sealedChat);   // the owner's own sealed chat, put back
        using var started = SealedApiNode.Over(files);
        using var client2 = started.Client();

        started.Gate.State.Should().Be(VaultState.Closed);
        var healthy = await client2.GetAsync("/health");
        healthy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await started.LoginAsync(client2, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();
        started.Gate.State.Should().Be(VaultState.Open);
        File.Delete(away);
    }

    [Fact]
    public async Task The_sealed_wal_of_a_node_that_was_open_when_it_stopped_starts_unlocks_and_is_replayed_without_a_refusal()
    {
        var files = await SealedApiDirBuilder.BuildAsync(template);
        var boot = new BootStore(Path.Combine(files.Dir, "beememorybank.boot.db"));
        var pageKey = new DatabaseKeyService(boot, new BootDbKeyStore(boot.Connections)).DerivePageKey(files.DatabaseKey, DbPurposes.Vault);

        // A live sealed connection with frames that were never checkpointed: what a node killed while open leaves.
        using var writer = SealedSqlite.OpenKeyed(Path.Combine(files.Dir, "beememorybank.db"), pageKey);
        writer.Execute("PRAGMA journal_mode=WAL");
        writer.Execute("PRAGMA wal_autocheckpoint=0");
        writer.Execute("CREATE TABLE wal_probe (x TEXT); INSERT INTO wal_probe VALUES ('written-before-the-kill')");
        File.Exists(Path.Combine(files.Dir, "beememorybank.db-wal")).Should().BeTrue();
        new FileInfo(Path.Combine(files.Dir, "beememorybank.db-wal")).Length.Should().BeGreaterThan(0);

        using var node = SealedApiNode.Over(files);
        using var client = node.Client();
        (await SealedApiNode.GetJsonAsync(client, "/api/vault/status")).GetProperty("mode").GetString().Should().Be("sealed");
        (await node.LoginAsync(client, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();

        var vault = node.Services.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>();
        using var read = vault.CreateConnection();
        read.ExecuteScalar<string>("SELECT x FROM wal_probe").Should().Be("written-before-the-kill");
    }

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f =>
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(s));
            });
}
