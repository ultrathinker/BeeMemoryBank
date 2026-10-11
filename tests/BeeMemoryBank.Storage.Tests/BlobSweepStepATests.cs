using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The blob garbage collector's Step A references (docs/design/step-a-spec.md 3.10, 9.6): a sealed event names its blobs only
/// in the envelope's clear "b" list, and a blind host's current baseline names its blobs in tbl_baseline_blob. Both keep
/// their blobs; an old blob neither names is still swept. One statement serves full and blind hosts.
/// </summary>
public class BlobSweepStepATests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private BlobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_blobsweep_a_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new BlobRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Sweep_KeepsTheBlobsASealedEventNames_InItsEnvelope()
    {
        var named = await OldBlobAsync();
        var other = await OldBlobAsync();
        var envelope = $$"""{"v":1,"k":"{{new string('a', 32)}}","n":"AAAAAAAAAAAAAAAA","b":["{{named}}"],"c":"AAAA"}""";
        await InsertEventAsync("sealed", envelope, protocol: 4);

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(1, "only the blob nothing names goes");
        (await _repo.GetAsync(named)).Should().NotBeNull("the sealed event needs it; its payload has no ciphertext_sha256");
        (await _repo.GetAsync(other)).Should().BeNull();
    }

    [Fact]
    public async Task Sweep_KeepsTheBlobsOfTheCurrentBaseline()
    {
        var listed = await OldBlobAsync();
        var other = await OldBlobAsync();
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("INSERT INTO tbl_baseline_blob (baseline_id, hash) VALUES (@id, @listed)",
                new { id = Guid.NewGuid().ToString(), listed });

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(1);
        (await _repo.GetAsync(listed)).Should().NotBeNull("the baseline a blind host serves and restores from needs it");
        (await _repo.GetAsync(other)).Should().BeNull();
    }

    [Fact]
    public async Task Sweep_IgnoresABListOfAnEventThatIsNotSealed()
    {
        var blob = await OldBlobAsync();
        await InsertEventAsync("whitelist_add", $$"""{"b":["{{blob}}"]}""", protocol: 3);

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(1,
            "only a sealed event's envelope names blobs by \"b\"; a control payload saying so references nothing");
    }

    private async Task<string> OldBlobAsync()
    {
        var hash = await _repo.StoreAsync(RandomNumberGenerator.GetBytes(64));
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_blob SET created_at = @t WHERE hash = @hash",
            new { t = DateTime.UtcNow.AddHours(-30).ToString("o"), hash });
        return hash;
    }

    private async Task InsertEventAsync(string type, string payload, int protocol)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, created_at, protocol_version)
              VALUES (@id, @node, 1, @type, @payload, X'00', @now, @protocol)",
            new { id = Guid.NewGuid().ToString(), node = Guid.NewGuid().ToString(), type, payload, now = DateTime.UtcNow.ToString("o"), protocol });
    }
}
