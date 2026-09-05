using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Item 16a, phase 1: migration 023 backfills tbl_media.ciphertext_sha256 from the media_create
/// event still in the log. The one thing that can silently make it a no-op is the Guid-case trap:
/// the event payload serializes media_id as a lowercase Guid while tbl_media.id is stored
/// uppercase, and SQLite compares TEXT case-sensitively — the exact shape that emptied every
/// article's tag list before it was caught. This pins that the join actually matches.
///
/// The migration runner has already applied 023 by the time a fresh DB opens, so these re-run the
/// backfill's own UPDATE against seeded rows rather than trying to un-apply a migration.
/// </summary>
public class MediaHashBackfillTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    // Verbatim body of migration 023's backfill statement. Kept in lockstep with the .sql on
    // purpose: if the migration's join predicate regresses, this fails.
    private const string BackfillSql = @"
        UPDATE tbl_media
        SET ciphertext_sha256 = (
            SELECT json_extract(e.payload, '$.ciphertext_sha256')
            FROM tbl_event e
            WHERE e.event_type = 'media_create'
              AND upper(json_extract(e.payload, '$.media_id')) = tbl_media.id
              AND json_extract(e.payload, '$.ciphertext_sha256') IS NOT NULL
            ORDER BY e.sequence_num DESC
            LIMIT 1
        )
        WHERE ciphertext_sha256 IS NULL;";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_mediabackfill_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task SeedMediaRowAsync(Guid id)
    {
        using var conn = _factory.CreateConnection();
        // Guids are stored uppercase (the app binds a Guid and lets the provider render it).
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size,
                  encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at)
              VALUES (@id, NULL, 'f.bin', 'application/octet-stream', 10,
                  @z, @z, @z, 'A', 1, NULL, @now)",
            new { id = id.ToString().ToUpperInvariant(), z = new byte[12], now = DateTime.UtcNow.ToString("o") });
    }

    private async Task SeedMediaCreateEventAsync(Guid mediaId, string ciphertextSha256)
    {
        using var conn = _factory.CreateConnection();
        // media_id serialized the way System.Text.Json does it: lowercase, hyphenated.
        var payload = $"{{\"media_id\":\"{mediaId.ToString().ToLowerInvariant()}\"," +
                      $"\"ciphertext_sha256\":\"{ciphertextSha256}\"}}";
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_event (event_id, node_id, event_type, article_id, payload, lamport_ts, signature, created_at)
              VALUES (@eventId, @node, 'media_create', NULL, @payload, 1, @sig, @now)",
            new { eventId = Guid.NewGuid().ToString(), node = Guid.NewGuid().ToString(), payload, sig = new byte[64], now = DateTime.UtcNow.ToString("o") });
    }

    [Fact]
    public async Task Backfill_PopulatesHash_AcrossTheUppercaseLowercaseGuidBoundary()
    {
        var mediaId = Guid.NewGuid();
        const string hash = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";
        await SeedMediaRowAsync(mediaId);
        await SeedMediaCreateEventAsync(mediaId, hash);

        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(BackfillSql);

        var got = await conn.QuerySingleAsync<string?>(
            "SELECT ciphertext_sha256 FROM tbl_media WHERE id = @id",
            new { id = mediaId.ToString().ToUpperInvariant() });
        got.Should().Be(hash, "the lowercase payload media_id must match the uppercase row id via upper()");
    }

    [Fact]
    public async Task Backfill_LeavesRowsWithNoMatchingEvent_Null()
    {
        var mediaId = Guid.NewGuid();
        await SeedMediaRowAsync(mediaId); // no event at all — pre-blob-store / compacted-away media

        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(BackfillSql);

        var got = await conn.QuerySingleAsync<string?>(
            "SELECT ciphertext_sha256 FROM tbl_media WHERE id = @id",
            new { id = mediaId.ToString().ToUpperInvariant() });
        got.Should().BeNull("a row with no media_create event keeps NULL and falls back to the .enc file");
    }
}
