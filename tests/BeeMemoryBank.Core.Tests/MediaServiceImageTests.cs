using BeeMemoryBank.Core.Services;
using MediaModel = BeeMemoryBank.Core.Models.Media;
using BeeMemoryBank.Media;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using static BeeMemoryBank.Core.Tests.ImageFixtures;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// What the media service does with an uploaded image on top of the transcoder: raster pictures become JPEG, an animated GIF and an SVG
/// are stored untouched, a file that is not an acceptable image is a client error (the upload endpoint answers an ArgumentException with 400).
/// </summary>
public class MediaServiceImageTests : IAsyncLifetime
{
    private DbConnectionFactory Factory { get; set; } = null!;
    private SessionService Session { get; set; } = null!;
    private MediaService MediaService { get; set; } = null!;
    private string TempMediaDir { get; set; } = "";
    private const string Uploader = "test-uploader";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        Factory = DbConnectionFactory.CreateInMemory($"bmb_mediaimg_{Guid.NewGuid():N}");
        await new MigrationRunner(Factory).RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var articleRepo = new ArticleRepository(Factory, scopeHolder);
        var keySlotRepo = new KeySlotRepository(Factory);
        var nodeRepo = new NodeIdentityRepository(Factory);
        var userRepo = new UserRepository(Factory);
        var mediaRepo = new MediaRepository(Factory, scopeHolder);

        Session = new SessionService(keySlotRepo);
        var init = new InitializationService(nodeRepo, keySlotRepo, userRepo, Factory);

        TempMediaDir = Path.Combine(Path.GetTempPath(), $"bmb_test_media_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempMediaDir);
        // The real event logger and blob store, so the stored bytes can be read back as in production.
        var clock = new NullLamportClock();
        var blobRepo = new BlobRepository(Factory);
        var eventLogger = new EventLogger(nodeRepo, new EventLogRepository(Factory), clock, new NullActorProvider(), new SyncTrigger(), Session, blobRepo);
        MediaService = new MediaService(mediaRepo, articleRepo, Session, nodeRepo,
            clock, eventLogger,
            new MediaStorageOptions(TempMediaDir), Factory, new SkiaImageTranscoder(), blobRepo: blobRepo);

        await init.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    public Task DisposeAsync()
    {
        Session.Lock();
        Factory.Dispose();
        if (Directory.Exists(TempMediaDir))
            Directory.Delete(TempMediaDir, true);
        return Task.CompletedTask;
    }

    private async Task<(MediaModel media, byte[] stored)> UploadAsync(string fileName, string contentType, byte[] bytes)
    {
        // An unlinked upload is read back by its uploader.
        var media = await MediaService.CreateAsync(fileName, contentType, bytes, null, uploadedBy: Uploader);
        var content = await MediaService.GetOwnedOrphanContentAsync(media.Id, Uploader);
        content.Should().NotBeNull();
        return (media, content!.Value.data);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("gif")]
    public async Task ARasterPicture_IsStoredAsAJpeg(string format)
    {
        var bytes = format switch { "png" => Png(80, 60), "webp" => Webp(80, 60), _ => Gif(80, 60) };

        var (media, stored) = await UploadAsync("picture." + format, "image/" + format, bytes);

        media.ContentType.Should().Be("image/jpeg");
        media.FileName.Should().Be("picture.jpg");
        IsJpeg(stored).Should().BeTrue();
        Size(stored).Should().Be((80, 60));
    }

    [Fact]
    public async Task AnAnimatedGif_IsStoredUntouched()
    {
        var gif = Gif(40, 30, frames: 3);

        var (media, stored) = await UploadAsync("spin.gif", "image/gif", gif);

        media.ContentType.Should().Be("image/gif");
        media.FileName.Should().Be("spin.gif");
        stored.Should().Equal(gif);
    }

    [Fact]
    public async Task AnSvg_IsStoredUntouched()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();

        var (media, stored) = await UploadAsync("logo.svg", "image/svg+xml", svg);

        media.ContentType.Should().Be("image/svg+xml");
        stored.Should().Equal(svg);
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("bomb")]
    public async Task APictureThatIsNotAcceptable_IsAClientError(string kind)
    {
        var bytes = kind == "junk" ? new byte[4096] : PngClaiming(60000, 60000);
        if (kind == "junk") new Random(3).NextBytes(bytes);

        var upload = () => MediaService.CreateAsync("bad.png", "image/png", bytes, null);

        await upload.Should().ThrowAsync<ArgumentException>();
    }
}
