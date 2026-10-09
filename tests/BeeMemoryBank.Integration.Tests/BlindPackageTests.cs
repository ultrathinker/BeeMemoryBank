using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The blind package (CONTRACTS §2, plan 3.6, 4.2): the signed peer snapshot, plus the hard-delete
/// audit, minus the embedding projections, with blind-manifest.json. Checked on the package itself —
/// what a blind node would actually receive — not on the events that built the database.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindPackageTests : IAsyncLifetime
{
    private const string Password = "blindPackagePw1!";
    private const string CanaryActorName = "Canary Actor Name";
    private const string CanaryAgentName = "Canary Agent Name";
    private const string CanaryPath = "/Canary/Secret/Path";
    private readonly BmbWebApplicationFactory _full = new();
    private string _extractDir = null!;

    public async Task InitializeAsync()
    {
        await _full.InitializeNodeAsync(password: Password);
        using var client = _full.CreateClient();
        (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
        _extractDir = Path.Combine(_full.DataPath, "extracted");
    }

    public Task DisposeAsync()
    {
        _full.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Package_HasNoProjections_KeepsHardDeleteAudit_AndCarriesASignedManifest()
    {
        using var scope = _full.Services.CreateScope();
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>()
            .CreateAsync("Secret plan", "/Notes", ["tag"], "the body");
        using (var conn = _full.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = X'0102030405060708'");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_hard_delete_audit (occurred_at, entity_type, entity_identifier)
                  VALUES (@at, 'article', 'gone-for-good')", new { at = DateTime.UtcNow.ToString("O") });
        }
        var self = (await _full.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var extracted = await _full.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(package.FilePath, _extractDir);

        extracted.IsSignedBy(self.Ed25519PublicKey).Should().BeTrue();
        var manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(_extractDir, BlindManifest.FileName)));
        manifest.ProducerNodeId.Should().Be(self.NodeId);
        manifest.Whitelist.Should().Contain(p => p.NodeId == self.NodeId && p.IsSuperadmin);

        using var db = new SqliteConnection($"Data Source={extracted.DatabasePath};Pooling=False");
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE title = @title", new { title = article.Title }))
            .Should().Be(1, "the content itself travels");
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE embedding_projection IS NOT NULL"))
            .Should().Be(0, "a projection is derived from the plaintext and must not reach a blind node");
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_hard_delete_audit"))
            .Should().Be(1, "the blind package keeps the hard-delete audit a join snapshot drops");
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'tbl_key_slot'"))
            .Should().Be(0, "secrets are filtered exactly as for a joining peer");
    }

    /// <summary>
    /// Review L-stage1 #1: "no projection" must hold for the bytes that travel, not just for a
    /// query — SQLite keeps updated-away values in free pages until the file is rewritten.
    /// </summary>
    [Fact]
    public async Task Package_Bytes_HoldNoTraceOfAProjection()
    {
        var sentinel = System.Text.Encoding.ASCII.GetBytes("BMB-PROJECTION-SENTINEL-4f1d9a7c-do-not-ship");
        // Projection-sized (a few KB), so it spills into overflow pages as a real one does — a nulled
        // value frees those pages without scrubbing them.
        var projection = Enumerable.Repeat(sentinel, 200).SelectMany(b => b).ToArray();
        using var scope = _full.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Plan", "/Notes", ["tag"], "body");
        using (var conn = _full.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = @p", new { p = projection });

        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var extracted = await _full.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(package.FilePath, _extractDir);
        var raw = await File.ReadAllBytesAsync(extracted.DatabasePath);
        var sidecars = Directory.GetFiles(_extractDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith("-wal") || f.EndsWith("-journal") || f.EndsWith("-shm")).ToList();

        raw.AsSpan().IndexOf(sentinel).Should().Be(-1, "the projection must not survive in any page of the packaged database");
        sidecars.Should().BeEmpty("the package is one self-contained database file");
    }

    /// <summary>
    /// A0 wire hygiene (brief P1-A0): concept-tag embeddings are derived from the plaintext tag name,
    /// exactly what the article projections are derived from, and travel with their model version —
    /// a blind node must hold neither.
    /// </summary>
    [Fact]
    public async Task Package_HasNoTagEmbeddings()
    {
        var sentinel = System.Text.Encoding.ASCII.GetBytes("BMB-TAG-EMBEDDING-SENTINEL-9a2c41be-do-not-ship");
        var embedding = Enumerable.Repeat(sentinel, 200).SelectMany(b => b).ToArray();
        using (var conn = _full.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                "INSERT INTO tbl_concept_tag (name, embedding, embedding_model_version) VALUES ('canary tag', @e, 'test-model-v1')",
                new { e = embedding });

        using var scope = _full.Services.CreateScope();
        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var extracted = await _full.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(package.FilePath, _extractDir);

        using var db = new SqliteConnection($"Data Source={extracted.DatabasePath};Pooling=False");
        (await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_concept_tag WHERE embedding IS NOT NULL OR embedding_model_version IS NOT NULL"))
            .Should().Be(0, "an embedding says something about the plaintext and must not reach a blind node");
        (await File.ReadAllBytesAsync(extracted.DatabasePath)).AsSpan().IndexOf(sentinel).Should().Be(-1,
            "nor may any byte of it survive in a page of the packaged database");
    }

    /// <summary>
    /// A0 wire hygiene, review round: the standing events ride in blind-manifest.json, serialized
    /// with the manifest's own options — not through SyncWire — so a legacy entity_id on a stored
    /// row would be relayed and the four envelope keys would appear as explicit nulls even where
    /// nothing was selected into them. The envelope may not leave the node here either.
    /// </summary>
    [Fact]
    public async Task Package_Manifest_CarriesNoEnvelopeFields()
    {
        var self = (await _full.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        // A standing whitelist event about this node, carrying the canary envelope values a real
        // row can hold. Nothing applies it here, so the signature is a placeholder.
        using (var scope = _full.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendIfNotExistsAsync(new SyncEvent
            {
                EventId = Guid.NewGuid(), NodeId = self.NodeId, LamportTs = 1,
                EventType = EventTypes.WhitelistAdd,
                Payload = JsonSerializer.Serialize(new { node_id = self.NodeId.ToString() }),
                ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow,
                Signature = new byte[64],
            });
        }
        using (var conn = _full.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                "UPDATE tbl_event SET actor_type = @t, actor_name = @n, via_agent_name = @a, entity_id = @e",
                new { t = "web", n = CanaryActorName, a = CanaryAgentName, e = CanaryPath });

        using var build = _full.Services.CreateScope();
        var package = await build.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        await _full.Services.GetRequiredService<SnapshotService>().ExtractVerifiedAsync(package.FilePath, _extractDir);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(_extractDir, BlindManifest.FileName));

        var manifest = BlindManifest.Parse(manifestBytes);
        manifest.Standing.Should().NotBeEmpty("the canary rides in a standing event about the producer");
        manifest.Standing.Should().OnlyContain(e => e.EntityId == null && e.ActorType == null
            && e.ActorName == null && e.ViaAgentName == null);
        var json = System.Text.Encoding.UTF8.GetString(manifestBytes);
        json.Should().NotContain(CanaryActorName).And.NotContain(CanaryAgentName).And.NotContain(CanaryPath);
        foreach (var key in new[] { "entityId", "EntityId", "actorType", "ActorType", "actorName", "ActorName", "viaAgentName", "ViaAgentName" })
            json.Should().NotContain($"\"{key}\"", "the manifest's own serialization must not carry the envelope either");
    }

    /// <summary>
    /// A blind copy takes the key a manifest names only for a row in pin mode (ADR 0007, review INT-03): a pin left beside the normal
    /// certificate, or beside a mode this build does not know, is not handed on to the copy.
    /// </summary>
    [Fact]
    public async Task Package_CarriesAPin_OnlyForARowInPinMode()
    {
        const string pin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var rows = new[] { (Id: Guid.NewGuid(), Trust: BlindTrust.Pin), (Id: Guid.NewGuid(), Trust: BlindTrust.PublicCa), (Id: Guid.NewGuid(), Trust: "future-mode") };
        using var scope = _full.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        foreach (var (id, trust) in rows)
            await whitelist.CreateAsync(new WhitelistEntry
            {
                NodeId = id, DisplayName = trust, Ed25519PublicKey = new byte[32], ApiAddress = $"https://{id:N}.example.org",
                TlsSpki = pin, TlsTrust = trust, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        await _full.Services.GetRequiredService<SnapshotService>().ExtractVerifiedAsync(package.FilePath, _extractDir);
        var manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(_extractDir, BlindManifest.FileName)));

        manifest.Whitelist.Where(p => p.NodeId == rows[0].Id).Select(p => (p.TlsTrust, p.TlsSpki)).Should().Equal((BlindTrust.Pin, pin));
        manifest.Whitelist.Where(p => p.NodeId == rows[1].Id).Select(p => (p.TlsTrust, p.TlsSpki)).Should().Equal((BlindTrust.PublicCa, (string?)null));
        manifest.Whitelist.Where(p => p.NodeId == rows[2].Id).Select(p => (p.TlsTrust, p.TlsSpki)).Should().Equal(("future-mode", (string?)null));
    }
}

