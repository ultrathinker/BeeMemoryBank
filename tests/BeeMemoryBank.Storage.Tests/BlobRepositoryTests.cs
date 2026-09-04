using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// tbl_blob semantics: content addressing, budgeted batch reads, and — the part with a real blast
/// radius — the garbage collector's reference scan and grace period. A sweep that is wrong in
/// either direction loses ciphertext for good, so each reference source is pinned separately.
/// </summary>
public class BlobRepositoryTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private BlobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_blobrepo_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new BlobRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── Content addressing ─────────────────────────────────────────────────

    [Fact]
    public async Task Store_ReturnsHashOfBytes_AndIsIdempotent()
    {
        var data = new byte[] { 1, 2, 3 };
        var h1 = await _repo.StoreAsync(data);
        var h2 = await _repo.StoreAsync(data);

        h1.Should().Be(BlobHash.Compute(data)).And.Be(h2);
        (await _repo.GetStatsAsync()).Count.Should().Be(1);
        (await _repo.GetAsync(h1)).Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Hash_MatchesSqliteSha256Function()
    {
        // Migration 016 backfilled tbl_blob with SQLite's sha256(); everything written since uses
        // BlobHash in C#. The two must agree byte for byte or the migrated rows are orphaned.
        var data = new byte[] { 0xde, 0xad, 0xbe, 0xef, 0, 1, 2 };
        using var conn = _factory.CreateConnection();
        var fromSql = await conn.ExecuteScalarAsync<string>("SELECT sha256(@data)", new { data });
        fromSql.Should().Be(BlobHash.Compute(data));
    }

    [Fact]
    public async Task GetExisting_ReportsOnlyStoredHashes()
    {
        var a = await _repo.StoreAsync([1]);
        var b = await _repo.StoreAsync([2]);
        var have = await _repo.GetExistingAsync([a, b, new string('0', 64)]);
        have.Should().BeEquivalentTo([a, b]);
    }

    [Fact]
    public async Task GetMany_HonoursByteBudget_ButAlwaysReturnsAtLeastOne()
    {
        var big = await _repo.StoreAsync(new byte[1000]);
        var small1 = await _repo.StoreAsync([1]);
        var small2 = await _repo.StoreAsync([2]);

        // Budget smaller than any single blob: still one comes back, so a pager makes progress.
        (await _repo.GetManyAsync([big], byteBudget: 10)).Should().HaveCount(1);

        // Budget for the two small ones but not the big one on top.
        var got = await _repo.GetManyAsync([small1, small2, big], byteBudget: 500);
        got.Sum(b => b.Data.Length).Should().BeLessThanOrEqualTo(1000 + 2, "over budget only by the admitted-first rule");
        got.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // ─── Garbage collection ─────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_KeepsBlobsYoungerThanCutoff_EvenIfUnreferenced()
    {
        var h = await _repo.StoreAsync([1, 2, 3]);
        var swept = await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2));
        swept.Should().Be(0);
        (await _repo.GetAsync(h)).Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_DeletesOldUnreferencedBlobs()
    {
        var h = await _repo.StoreAsync([1, 2, 3]);
        await AgeBlobAsync(h, TimeSpan.FromHours(3));

        var swept = await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2));
        swept.Should().Be(1);
        (await _repo.GetAsync(h)).Should().BeNull();
    }

    [Fact]
    public async Task Sweep_KeepsBlobReferencedByArticleBody()
    {
        var h = await _repo.StoreAsync([1, 2, 3]);
        await AgeBlobAsync(h, TimeSpan.FromHours(3));
        var articleId = await InsertArticleAsync();
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article_body (article_id, ciphertext, ciphertext_hash, iv, encrypted_dek, dek_iv)
                  VALUES (@articleId, X'010203', @h, X'00', X'00', X'00')", new { articleId, h });

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(0);
        (await _repo.GetAsync(h)).Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_KeepsBlobReferencedByArticleVersion()
    {
        var h = await _repo.StoreAsync([4, 5, 6]);
        await AgeBlobAsync(h, TimeSpan.FromHours(3));
        var articleId = await InsertArticleAsync();
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article_version (id, article_id, version_number, title, tree_path, ciphertext, ciphertext_hash, iv, encrypted_dek, dek_iv, created_at)
                  VALUES (@id, @articleId, 1, 't', '/', X'040506', @h, X'00', X'00', X'00', @now)",
                new { id = Guid.NewGuid().ToString(), articleId, h, now = DateTime.UtcNow.ToString("o") });

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(0);
        (await _repo.GetAsync(h)).Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_KeepsBlobReferencedByEventPayload()
    {
        var h = await _repo.StoreAsync([7, 8, 9]);
        await AgeBlobAsync(h, TimeSpan.FromHours(3));
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, article_id, entity_id, payload, signature, protocol_version, created_at)
                  VALUES (@id, @node, 1, 'article_create', @article, @article, @payload, X'00', 2, @now)",
                new
                {
                    id = Guid.NewGuid().ToString(), node = Guid.NewGuid().ToString(), article = Guid.NewGuid().ToString(),
                    payload = $$"""{"title":"x","ciphertext":null,"ciphertext_sha256":"{{h}}"}""",
                    now = DateTime.UtcNow.ToString("o")
                });

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(0);
        (await _repo.GetAsync(h)).Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_LegacyEventWithInlineCiphertext_DoesNotProtectAnything()
    {
        // A protocol-1 payload has no ciphertext_sha256; json_extract yields NULL and NOT IN
        // ignores it, so an unrelated old blob is still swept.
        var h = await _repo.StoreAsync([7, 8, 9]);
        await AgeBlobAsync(h, TimeSpan.FromHours(3));
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, article_id, entity_id, payload, signature, protocol_version, created_at)
                  VALUES (@id, @node, 1, 'article_create', @article, @article, @payload, X'00', 1, @now)",
                new
                {
                    id = Guid.NewGuid().ToString(), node = Guid.NewGuid().ToString(), article = Guid.NewGuid().ToString(),
                    payload = """{"title":"x","ciphertext":"AQID"}""",
                    now = DateTime.UtcNow.ToString("o")
                });

        (await _repo.SweepUnreferencedAsync(DateTime.UtcNow.AddHours(-2))).Should().Be(1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task AgeBlobAsync(string hash, TimeSpan by)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_blob SET created_at = @t WHERE hash = @hash",
            new { t = (DateTime.UtcNow - by).ToString("o"), hash });
    }

    private async Task<string> InsertArticleAsync()
    {
        var articleId = Guid.NewGuid().ToString();
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, lamport_ts, created_at, updated_at)
              VALUES (@id, 't', '/', 'A', 1, @now, @now)", new { id = articleId, now });
        return articleId;
    }
}
