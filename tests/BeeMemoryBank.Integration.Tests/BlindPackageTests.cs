using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
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
}
