using System.IO.Compression;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Covers the Этап "Bee-Export/Import" additions to <see cref="ZipExportService"/>: empty
/// folders now survive export (via .bmb-keep markers) and every export carries a
/// ".bmb-manifest.json" sidecar with exact titles/tags/protected-flags so
/// <see cref="BeeImportService"/> can restore them exactly. The round-trip test at the bottom
/// proves both services agree on the format by exporting from one in-memory vault and importing
/// into a completely separate one - the real "move a folder between two BeeMemoryBank nodes"
/// scenario this feature exists for.
/// </summary>
public class ZipExportServiceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;
    private IFolderRepository _folderRepo = null!;
    private ConceptTagService _conceptTagService = null!;
    private ZipExportService _exportService = null!;
    private MediaService _mediaService = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_zipexport_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var articleRepo = new ArticleRepository(_factory, scopeHolder);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        var userRepo = new UserRepository(_factory);
        var mediaRepo = new MediaRepository(_factory, scopeHolder);
        _folderRepo = new FolderRepository(_factory, scopeHolder);
        var versionRepo = new ArticleVersionRepository(_factory, scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, scopeHolder);
        _conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());

        _session = new SessionService(keySlotRepo);
        var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, _factory);
        // Real EventLogger + BlobRepository so media ciphertext actually lands in the blob store,
        // the way production does — media has no on-disk .enc home any more (16b), so a NullEventLogger
        // would leave the bytes nowhere and every media read would 404.
        var blobRepo = new BlobRepository(_factory);
        var mediaEventLogger = new EventLogger(nodeRepo, new EventLogRepository(_factory),
            new NullLamportClock(), new NullActorProvider(), new SyncTrigger(), _session, blobRepo);
        _mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo,
            new NullLamportClock(), mediaEventLogger, new MediaStorageOptions(Path.GetTempPath()), _factory,
            blobRepo: blobRepo);

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo,
            new NullLamportClock(), new NullEventLogger(), mediaRepo, _folderRepo,
            versionRepo, new NullActorProvider(), _conceptTagService, _factory);

        _exportService = new ZipExportService(_articleService, _mediaService, _conceptTagService, _folderRepo, scopeHolder);

        await initService.InitializeAsync("admin", "TestNode", "password");
        await _session.UnlockAsync("password");
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static Dictionary<string, ZipArchiveEntry> ReadEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.ToDictionary(e => e.FullName, e => e);
    }

    private static string ReadEntryText(string zipPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Entry '{entryName}' not found.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task ExportFolder_EmptySubfolder_GetsKeepMarkerAndManifestEntry()
    {
        await _articleService.CreateAsync("Doc", "/Мой гитхаб", ["tag-a"], "content");
        await _folderRepo.EnsureExistsAsync("/Мой гитхаб/Аудиты/2026-07-19", null); // empty, no articles

        var (zipPath, fileName) = await _exportService.ExportFolderAsync("/Мой гитхаб", withImages: false, CancellationToken.None);
        try
        {
            fileName.Should().Be("Мой гитхаб.zip");
            var entries = ReadEntries(zipPath);

            entries.Should().ContainKey(".bmb-manifest.json");
            entries.Should().ContainKey("Аудиты/2026-07-19/.bmb-keep");

            var manifestJson = ReadEntryText(zipPath, ".bmb-manifest.json");
            using var doc = System.Text.Json.JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;
            root.GetProperty("sourceFolderName").GetString().Should().Be("Мой гитхаб");

            var folders = root.GetProperty("folders").EnumerateArray().Select(e => e.GetString()).ToList();
            folders.Should().Contain("Аудиты/2026-07-19");
            folders.Should().Contain(""); // the exported folder itself

            var articles = root.GetProperty("articles").EnumerateArray().ToList();
            articles.Should().ContainSingle();
            articles[0].GetProperty("title").GetString().Should().Be("Doc");
            articles[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Contain("tag-a");
            articles[0].GetProperty("protected").GetBoolean().Should().BeFalse();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ExportFolder_NonEmptyFolder_DoesNotGetKeepMarker()
    {
        await _articleService.CreateAsync("Doc", "/Root", [], "content");

        var (zipPath, _) = await _exportService.ExportFolderAsync("/Root", withImages: false, CancellationToken.None);
        try
        {
            var entries = ReadEntries(zipPath);
            entries.Should().NotContainKey(".bmb-keep");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task RoundTrip_ExportFromOneVault_ImportIntoAnother_PreservesEverything()
    {
        // Simulate the real scenario: two SEPARATE BeeMemoryBank vaults (two in-memory DBs).
        using var otherVaultFactory = DbConnectionFactory.CreateInMemory($"bmb_zipexport_other_{Guid.NewGuid():N}");
        await new MigrationRunner(otherVaultFactory).RunMigrationsAsync();

        var otherScopeHolder = new CallerScopeHolder();
        var otherArticleRepo = new ArticleRepository(otherVaultFactory, otherScopeHolder);
        var otherBodyRepo = new ArticleBodyRepository(otherVaultFactory);
        var otherKeySlotRepo = new KeySlotRepository(otherVaultFactory);
        var otherNodeRepo = new NodeIdentityRepository(otherVaultFactory);
        var otherUserRepo = new UserRepository(otherVaultFactory);
        var otherMediaRepo = new MediaRepository(otherVaultFactory, otherScopeHolder);
        var otherFolderRepo = new FolderRepository(otherVaultFactory, otherScopeHolder);
        var otherVersionRepo = new ArticleVersionRepository(otherVaultFactory, otherScopeHolder);
        var otherConceptTagRepo = new ConceptTagRepository(otherVaultFactory, otherScopeHolder);
        var otherConceptTagService = new ConceptTagService(otherConceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var otherSession = new SessionService(otherKeySlotRepo);
        var otherInit = new InitializationService(otherNodeRepo, otherKeySlotRepo, otherUserRepo, otherVaultFactory);
        var otherBlobRepo = new BlobRepository(otherVaultFactory);
        var otherMediaEventLogger = new EventLogger(otherNodeRepo, new EventLogRepository(otherVaultFactory),
            new NullLamportClock(), new NullActorProvider(), new SyncTrigger(), otherSession, otherBlobRepo);
        var otherMediaService = new MediaService(otherMediaRepo, otherArticleRepo, otherSession, otherNodeRepo,
            new NullLamportClock(), otherMediaEventLogger, new MediaStorageOptions(Path.GetTempPath()), otherVaultFactory,
            blobRepo: otherBlobRepo);
        var otherArticleService = new ArticleService(otherArticleRepo, otherBodyRepo, otherSession, otherNodeRepo,
            new NullLamportClock(), new NullEventLogger(), otherMediaRepo, otherFolderRepo,
            otherVersionRepo, new NullActorProvider(), otherConceptTagService, otherVaultFactory);
        await otherInit.InitializeAsync("admin", "OtherNode", "password2");
        await otherSession.UnlockAsync("password2");
        var importService = new BeeImportService(otherArticleService, otherMediaService, otherFolderRepo, otherNodeRepo);

        // --- Build the source vault's content ---
        await _articleService.CreateAsync("Первая статья", "/Мой гитхаб", ["личное", "важное"], "Текст первой статьи.");
        await _articleService.CreateAsync("Вторая статья", "/Мой гитхаб/Подпапка", [], "Текст второй.");
        await _folderRepo.EnsureExistsAsync("/Мой гитхаб/Пустая", null);

        var (zipPath, _) = await _exportService.ExportFolderAsync("/Мой гитхаб", withImages: false, CancellationToken.None);
        try
        {
            using var zipStream = File.OpenRead(zipPath);
            var report = await importService.ImportAsync(zipStream, "/Work", CancellationToken.None);

            report.RootFolderPath.Should().Be("/Work/Мой гитхаб");
            report.ArticlesCreated.Should().Be(2);
            report.FoldersCreated.Should().BeGreaterThanOrEqualTo(2); // "Подпапка" + "Пустая" at least

            var topLevel = (await otherArticleService.ListAsync("/Work/Мой гитхаб"))
                .Where(a => a.TreePath == "/Work/Мой гитхаб").ToList();
            topLevel.Should().ContainSingle(a => a.Title == "Первая статья");

            var nested = (await otherArticleService.ListAsync("/Work/Мой гитхаб/Подпапка"))
                .Where(a => a.TreePath == "/Work/Мой гитхаб/Подпапка").ToList();
            nested.Should().ContainSingle(a => a.Title == "Вторая статья");

            var tags = await otherConceptTagService.GetByArticleIdAsync(topLevel[0].Id);
            tags.Should().Contain(["личное", "важное"]);

            (await otherFolderRepo.GetByPathAsync("/Work/Мой гитхаб/Пустая")).Should().NotBeNull();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task RoundTrip_ArticleWithAttachment_ExportFromOneVault_ImportIntoAnother_AttachmentSurvives()
    {
        // Simulate the real scenario: two SEPARATE BeeMemoryBank vaults (two in-memory DBs).
        using var otherVaultFactory = DbConnectionFactory.CreateInMemory($"bmb_zipexport_other_{Guid.NewGuid():N}");
        await new MigrationRunner(otherVaultFactory).RunMigrationsAsync();

        var otherScopeHolder = new CallerScopeHolder();
        var otherArticleRepo = new ArticleRepository(otherVaultFactory, otherScopeHolder);
        var otherBodyRepo = new ArticleBodyRepository(otherVaultFactory);
        var otherKeySlotRepo = new KeySlotRepository(otherVaultFactory);
        var otherNodeRepo = new NodeIdentityRepository(otherVaultFactory);
        var otherUserRepo = new UserRepository(otherVaultFactory);
        var otherMediaRepo = new MediaRepository(otherVaultFactory, otherScopeHolder);
        var otherFolderRepo = new FolderRepository(otherVaultFactory, otherScopeHolder);
        var otherVersionRepo = new ArticleVersionRepository(otherVaultFactory, otherScopeHolder);
        var otherConceptTagRepo = new ConceptTagRepository(otherVaultFactory, otherScopeHolder);
        var otherConceptTagService = new ConceptTagService(otherConceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var otherSession = new SessionService(otherKeySlotRepo);
        var otherInit = new InitializationService(otherNodeRepo, otherKeySlotRepo, otherUserRepo, otherVaultFactory);
        var otherBlobRepo = new BlobRepository(otherVaultFactory);
        var otherMediaEventLogger = new EventLogger(otherNodeRepo, new EventLogRepository(otherVaultFactory),
            new NullLamportClock(), new NullActorProvider(), new SyncTrigger(), otherSession, otherBlobRepo);
        var otherMediaService = new MediaService(otherMediaRepo, otherArticleRepo, otherSession, otherNodeRepo,
            new NullLamportClock(), otherMediaEventLogger, new MediaStorageOptions(Path.GetTempPath()), otherVaultFactory,
            blobRepo: otherBlobRepo);
        var otherArticleService = new ArticleService(otherArticleRepo, otherBodyRepo, otherSession, otherNodeRepo,
            new NullLamportClock(), new NullEventLogger(), otherMediaRepo, otherFolderRepo,
            otherVersionRepo, new NullActorProvider(), otherConceptTagService, otherVaultFactory);
        await otherInit.InitializeAsync("admin", "OtherNode", "password2");
        await otherSession.UnlockAsync("password2");
        var importService = new BeeImportService(otherArticleService, otherMediaService, otherFolderRepo, otherNodeRepo);

        // --- Build the source vault's content: an article with one inline image AND one
        // generic file attachment. Unlike the image, the attachment is never referenced from the
        // body - this is exactly the case BeeImportService used to silently drop.
        var article = await _articleService.CreateAsync("С файлом", "/Мой гитхаб", [], "Текст статьи.");
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var image = await _mediaService.CreateAsync("pic.png", "image/png", pngBytes, article.Id);
        await _articleService.UpdateAsync(article.Id, plaintext: $"Текст статьи. ![pic](/api/media/{image.Id})");
        var attachmentBytes = System.Text.Encoding.UTF8.GetBytes("hello attachment");
        var attachment = await _mediaService.CreateAsync("notes.txt", "text/plain", attachmentBytes, article.Id, isAttachment: true);

        var (zipPath, _) = await _exportService.ExportFolderAsync("/Мой гитхаб", withImages: true, CancellationToken.None);
        try
        {
            using var zipStream = File.OpenRead(zipPath);
            var report = await importService.ImportAsync(zipStream, "/Work", CancellationToken.None);

            report.ArticlesCreated.Should().Be(1);
            report.ImagesImported.Should().Be(1);
            report.AttachmentsImported.Should().Be(1);
            report.Warnings.Should().BeEmpty();

            var imported = (await otherArticleService.ListAsync("/Work/Мой гитхаб"))
                .Single(a => a.Title == "С файлом");

            var importedMedia = await otherMediaService.GetByArticleIdAsync(imported.Id);
            importedMedia.Should().HaveCount(2);

            var importedAttachment = importedMedia.Should().ContainSingle(m => m.Kind == "attachment").Which;
            importedAttachment.FileName.Should().Be(attachment.FileName);
            var attachmentContent = await otherMediaService.GetContentAsync(importedAttachment.Id);
            attachmentContent.Should().NotBeNull();
            attachmentContent!.Value.data.Should().Equal(attachmentBytes);

            var importedImage = importedMedia.Should().ContainSingle(m => m.Kind == "image").Which;
            importedImage.FileName.Should().Be(image.FileName);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }
}
