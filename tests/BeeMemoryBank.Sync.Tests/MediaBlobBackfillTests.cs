using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Item 16b, phase 1: the disk backfill that moves legacy media (ciphertext living ONLY as an
/// .enc file, with a null row hash) into the content-addressed blob store, so the blob store
/// becomes the complete home for media before any .enc file is ever deleted.
///
/// Real repositories + real <see cref="BlobRepository"/> and <see cref="MediaService"/> — the
/// legacy state is produced from the genuine create path and then stripped back to file-only, not
/// hand-planted, so the test exercises exactly what a pre-16a row looks like on disk.
/// </summary>
public class MediaBlobBackfillTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private string _mediaDir = null!;
    private MediaService _media = null!;
    private BlobRepository _blobs = null!;
    private MediaRepository _mediaRepo = null!;
    private MediaBlobBackfillService _backfill = null!;
    private const string Password = "mediaBackfillTestPassword";

    private static readonly byte[] Payload =
        System.Text.Encoding.UTF8.GetBytes("item-16b legacy media payload — только байты, не картинка");

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_mediabackfill_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        var userRepo = new UserRepository(_factory);
        var session = new SessionService(keySlotRepo);
        await new InitializationService(nodeRepo, keySlotRepo, userRepo, _factory)
            .InitializeAsync("admin", "MediaNode", Password);
        await session.UnlockAsync(Password);

        _blobs = new BlobRepository(_factory);
        _mediaRepo = new MediaRepository(_factory, scopeHolder);
        var eventLogRepo = new EventLogRepository(_factory);
        ILamportClock clock = new LamportClock();
        var eventLogger = new EventLogger(nodeRepo, eventLogRepo, clock,
            new NullActorProvider(), new SyncTrigger(), session, _blobs);

        var vectorCache = new EmbeddingVectorCache(_factory);
        var articleRepo = new ArticleRepository(_factory, scopeHolder, vectorCache);

        _mediaDir = Path.Combine(Path.GetTempPath(), "bmb-mediabackfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mediaDir);

        _media = new MediaService(_mediaRepo, articleRepo, session, nodeRepo, clock, eventLogger,
            new MediaStorageOptions(_mediaDir), _factory, logger: null, blobRepo: _blobs);
        _backfill = new MediaBlobBackfillService(_factory, _blobs, new MediaStorageOptions(_mediaDir), logger: null);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        try { Directory.Delete(_mediaDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    /// <summary>Creates media through the real path, then strips it back to the pre-16a shape:
    /// row hash null, no blob, only the .enc file on disk.</summary>
    private async Task<(Guid id, string hash)> CreateLegacyFileOnlyMediaAsync()
    {
        var media = await _media.CreateAsync("f.bin", "application/octet-stream", Payload, articleId: null, isAttachment: true);
        var hash = media.CiphertextSha256!;
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_media SET ciphertext_sha256 = NULL WHERE id = @id", new { id = media.Id });
        await conn.ExecuteAsync("DELETE FROM tbl_blob WHERE hash = @hash", new { hash });
        return (media.Id, hash);
    }

    private string EncPath(Guid id) => Path.Combine(_mediaDir, id.ToString() + ".enc");

    [Fact]
    public async Task LegacyFileOnlyMedia_IsMovedIntoTheBlobStore_AndStaysReadable()
    {
        var (id, hash) = await CreateLegacyFileOnlyMediaAsync();

        // Precondition: this is genuinely file-only now.
        (await _blobs.GetAsync(hash)).Should().BeNull("the blob was stripped to simulate a pre-16a row");
        File.Exists(EncPath(id)).Should().BeTrue("the .enc file is the only copy");
        (await _mediaRepo.GetByIdAsync(id))!.CiphertextSha256.Should().BeNull();

        var result = await _backfill.BackfillAsync();

        result.Stored.Should().Be(1);
        result.MissingFile.Should().Be(0);

        var row = await _mediaRepo.GetByIdAsync(id);
        row!.CiphertextSha256.Should().Be(hash, "the bytes hash to the same content address");
        (await _blobs.GetAsync(hash)).Should().NotBeNull("the ciphertext now lives in the blob store");

        // The read path (blob-first) can now serve it, and the plaintext round-trips.
        var content = await _media.GetContentAsync(id);
        content.Should().NotBeNull();
        content!.Value.data.Should().Equal(Payload);
    }

    [Fact]
    public async Task ASecondPass_IsANoOp()
    {
        await CreateLegacyFileOnlyMediaAsync();
        (await _backfill.BackfillAsync()).Stored.Should().Be(1);

        var second = await _backfill.BackfillAsync();
        second.Stored.Should().Be(0, "every row already carries its hash");
        second.MissingFile.Should().Be(0);
    }

    [Fact]
    public async Task ARowWhoseFileIsGone_IsReportedMissing_NotFailed()
    {
        var (id, _) = await CreateLegacyFileOnlyMediaAsync();
        File.Delete(EncPath(id)); // bytes exist only on a peer — nothing to backfill locally

        var result = await _backfill.BackfillAsync();

        result.Stored.Should().Be(0);
        result.MissingFile.Should().Be(1);
        (await _mediaRepo.GetByIdAsync(id))!.CiphertextSha256
            .Should().BeNull("with no local file there is nothing to stamp — the row is left for a sync pull");
    }

    [Fact]
    public async Task AFreshlyCreatedMedia_NeedsNoBackfill()
    {
        // The create path already stamps the hash and stores the blob, so a normal row must not
        // be re-touched — the backfill is only for the pre-16a legacy shape.
        await _media.CreateAsync("f.bin", "application/octet-stream", Payload, articleId: null, isAttachment: true);

        (await _backfill.BackfillAsync()).Stored.Should().Be(0);
    }
}
