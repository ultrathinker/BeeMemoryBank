using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Item 16a, phase 1: a media row carries the content-address of its ciphertext, the read path
/// resolves the bytes from the content-addressed blob store, and the blob GC keeps a blob alive as
/// long as a media row points at it (so event compaction can no longer strand the only copy).
///
/// These run against real repositories + a real <see cref="EventLogger"/> and
/// <see cref="BlobRepository"/> — the blob is stored by the exact path production uses
/// (LogMediaCreateAsync → EnsureBlobAsync), not planted by the test.
/// </summary>
public class MediaBlobStorageTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private string _mediaDir = null!;
    private MediaService _media = null!;
    private BlobRepository _blobs = null!;
    private MediaRepository _mediaRepo = null!;
    private const string Password = "mediaBlobTestPassword";

    // Arbitrary non-image bytes uploaded as an attachment: the attachment path skips image
    // re-encoding, so the stored plaintext equals the input byte-for-byte and assertions are exact.
    private static readonly byte[] Payload =
        System.Text.Encoding.UTF8.GetBytes("item-16a attachment payload — не картинка, просто байты");

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_mediablob_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder(); // default scope = system (no ACL), superadmin
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

        _mediaDir = Path.Combine(Path.GetTempPath(), "bmb-mediablob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mediaDir);

        _media = new MediaService(_mediaRepo, articleRepo, session, nodeRepo, clock, eventLogger,
            new MediaStorageOptions(_mediaDir), _factory, logger: null, blobRepo: _blobs);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        try { Directory.Delete(_mediaDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private Task<Core.Models.Media> CreateAttachmentAsync() =>
        _media.CreateAsync("note.bin", "application/octet-stream", Payload, articleId: null, isAttachment: true);

    private string EncPath(Guid id) => Path.Combine(_mediaDir, $"{id}.enc");

    [Fact]
    public async Task Create_RecordsTheCiphertextHash_AndStoresTheBlobUnderIt()
    {
        var media = await CreateAttachmentAsync();

        media.CiphertextSha256.Should().NotBeNullOrEmpty("the create path records the blob hash on the row");

        var stored = await _mediaRepo.GetByIdAsync(media.Id);
        stored!.CiphertextSha256.Should().Be(media.CiphertextSha256, "the hash is persisted, not just set in memory");

        (await _blobs.GetAsync(media.CiphertextSha256!)).Should().NotBeNull(
            "the ciphertext blob is stored under exactly that hash");
    }

    [Fact]
    public async Task Read_ResolvesFromTheBlob_WhenTheEncFileIsGone()
    {
        var media = await CreateAttachmentAsync();

        // Remove the legacy file home entirely. Only the blob can satisfy the read now.
        File.Delete(EncPath(media.Id));
        File.Exists(EncPath(media.Id)).Should().BeFalse();

        var result = await _media.GetContentAsync(media.Id);

        result.Should().NotBeNull("the bytes still live in the blob store");
        result!.Value.data.Should().Equal(Payload);
    }

    [Fact]
    public async Task Read_FallsBackToTheFile_WhenTheRowHasNoHash()
    {
        var media = await CreateAttachmentAsync();

        // Simulate a pre-blob-store (legacy) row: an .enc file left on disk by the old dual-write
        // code, and a null hash. The create path no longer writes .enc (16b), so the file is
        // planted here from the blob's bytes to reproduce exactly that legacy shape.
        var ciphertext = await _blobs.GetAsync(media.CiphertextSha256!);
        ciphertext.Should().NotBeNull();
        await File.WriteAllBytesAsync(Path.Combine(_mediaDir, media.Id + ".enc"), ciphertext!);
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_media SET ciphertext_sha256 = NULL WHERE id = @id",
                new { id = media.Id });

        var result = await _media.GetContentAsync(media.Id);

        result.Should().NotBeNull("the .enc file is still the home for rows without a hash");
        result!.Value.data.Should().Equal(Payload);
    }

    [Fact]
    public async Task Sweep_KeepsABlobReferencedOnlyByALiveMediaRow_AfterItsEventIsCompactedAway()
    {
        var media = await CreateAttachmentAsync();
        var mediaHash = media.CiphertextSha256!;

        // The whole point of item 16a's sweep clause: once the media_create event is gone, the
        // media ROW is the only thing that references the blob. Simulate compaction by deleting the
        // event — otherwise the event's own ciphertext_sha256 reference would protect the blob and
        // this test would pass without exercising the media clause at all.
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("DELETE FROM tbl_event WHERE event_type = 'media_create'");

        // An unreferenced blob of the same age, to prove the sweep is actually running and would
        // have taken the media blob too if the media reference did not protect it.
        var strayHash = await _blobs.StoreAsync(System.Text.Encoding.UTF8.GetBytes("nobody references me"));

        // Cutoff in the future so the grace period does not shield either blob.
        var swept = await _blobs.SweepUnreferencedAsync(DateTime.UtcNow.AddMinutes(5));

        swept.Should().BeGreaterThan(0);
        (await _blobs.GetAsync(strayHash)).Should().BeNull("an unreferenced blob is collected");
        (await _blobs.GetAsync(mediaHash)).Should().NotBeNull(
            "a blob a live media row points at survives GC even after its create event is compacted away");
    }
}
