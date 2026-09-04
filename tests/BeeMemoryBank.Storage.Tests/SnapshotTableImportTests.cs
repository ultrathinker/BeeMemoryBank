using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The restore/join importer must copy tables by column NAME. Before this helper existed every
/// restore path ran <c>INSERT INTO t SELECT * FROM snap.t</c>, which is positional — and once
/// migration 017 dropped the inline ciphertext column, a snapshot from an older schema would
/// either fail on column count (016 snapshot) or, with the SAME count (pre-016 snapshot),
/// silently write ciphertext into iv, iv into encrypted_dek and dek_iv into ciphertext_hash.
/// Each case here builds a snapshot database in the old shape and imports it into a current one.
/// </summary>
public class SnapshotTableImportTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private string _snapPath = null!;

    private static readonly byte[] Body = [10, 20, 30, 40];
    private static readonly byte[] Iv = [1, 2, 3];
    private static readonly byte[] Dek = [4, 5, 6];
    private static readonly byte[] DekIv = [7, 8, 9];
    private static readonly string ArticleId = Guid.NewGuid().ToString();

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_snapimport_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _snapPath = Path.Combine(Path.GetTempPath(), $"bmb_snap_{Guid.NewGuid():N}.db");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        try { File.Delete(_snapPath); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Pre016Snapshot_SameColumnCount_ImportsByNameAndAdoptsInlineCiphertext()
    {
        // Pre-016 shape: 5 columns, ciphertext inline, no hash column, no tbl_blob at all.
        await BuildSnapshotAsync(
            "CREATE TABLE tbl_article_body (article_id TEXT PRIMARY KEY, ciphertext BLOB NOT NULL, iv BLOB NOT NULL, encrypted_dek BLOB NOT NULL, dek_iv BLOB NOT NULL)",
            "INSERT INTO tbl_article_body VALUES (@id, @body, @iv, @dek, @dekIv)");

        await ImportAsync();

        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleAsync(
            "SELECT b.iv AS Iv, b.encrypted_dek AS Dek, b.dek_iv AS DekIv, b.ciphertext_hash AS Hash, bl.data AS Data " +
            "FROM tbl_article_body b LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @id",
            new { id = ArticleId });
        ((byte[])row.Iv).Should().Equal(Iv, "positional import would have put the ciphertext here");
        ((byte[])row.Dek).Should().Equal(Dek);
        ((byte[])row.DekIv).Should().Equal(DekIv);
        ((string)row.Hash).Should().Be(BlobHash.Compute(Body));
        ((byte[])row.Data).Should().Equal(Body, "the inline bytes must have been folded into tbl_blob");
    }

    [Fact]
    public async Task Migration016Snapshot_ExtraColumn_ImportsWithoutCountMismatch()
    {
        // 016 shape: hash column present AND inline ciphertext still there (6 columns).
        var hash = BlobHash.Compute(Body);
        await BuildSnapshotAsync(
            "CREATE TABLE tbl_article_body (article_id TEXT PRIMARY KEY, ciphertext BLOB NOT NULL, iv BLOB NOT NULL, encrypted_dek BLOB NOT NULL, dek_iv BLOB NOT NULL, ciphertext_hash TEXT); " +
            "CREATE TABLE tbl_blob (hash TEXT PRIMARY KEY, data BLOB NOT NULL, size INTEGER NOT NULL, created_at TEXT NOT NULL)",
            "INSERT INTO tbl_article_body VALUES (@id, @body, @iv, @dek, @dekIv, @hash); " +
            "INSERT INTO tbl_blob VALUES (@hash, @body, length(@body), 'now')",
            hash);

        await ImportAsync(includeBlobTable: true);

        using var conn = _factory.CreateConnection();
        var data = await conn.QuerySingleAsync<byte[]>(
            "SELECT bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @id",
            new { id = ArticleId });
        data.Should().Equal(Body);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task BuildSnapshotAsync(string schema, string insert, string? hash = null)
    {
        using var snap = new SqliteConnection($"Data Source={_snapPath}");
        snap.Open();
        await snap.ExecuteAsync(schema);
        await snap.ExecuteAsync(insert, new { id = ArticleId, body = Body, iv = Iv, dek = Dek, dekIv = DekIv, hash });
    }

    private async Task ImportAsync(bool includeBlobTable = false)
    {
        using var conn = (SqliteConnection)_factory.CreateConnection();
        await conn.ExecuteAsync("PRAGMA foreign_keys = OFF");
        await conn.ExecuteAsync($"ATTACH DATABASE '{_snapPath.Replace("'", "''")}' AS snap");
        // The FK to tbl_article is irrelevant here; the article row is not what is under test.
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 't', '/', 'A', 'now', 'now')",
            new { id = ArticleId });
        using var tx = conn.BeginTransaction();
        if (includeBlobTable) SnapshotTableImport.CopyTable(conn, tx, "tbl_blob", orIgnore: true);
        SnapshotTableImport.CopyTable(conn, tx, "tbl_article_body", orIgnore: true);
        SnapshotTableImport.AdoptLegacyInlineCiphertext(conn, tx);
        tx.Commit();
    }
}
