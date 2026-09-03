using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using SixLabors.ImageSharp;

namespace BeeMemoryBank.Core.Tests;

public class BeeImportServiceTests : IAsyncLifetime
{
    private DbConnectionFactory Factory { get; set; } = null!;
    private SessionService Session { get; set; } = null!;
    private InitializationService InitService { get; set; } = null!;
    private ArticleService ArticleService { get; set; } = null!;
    private MediaService MediaService { get; set; } = null!;
    private IFolderRepository FolderRepo { get; set; } = null!;
    private BeeImportService ImportService { get; set; } = null!;
    private string TempMediaDir { get; set; } = "";

    private static readonly JsonSerializerOptions ManifestJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        Factory = DbConnectionFactory.CreateInMemory($"bmb_beeimport_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(Factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var articleRepo = new ArticleRepository(Factory, scopeHolder);
        var bodyRepo = new ArticleBodyRepository(Factory);
        var keySlotRepo = new KeySlotRepository(Factory);
        var nodeRepo = new NodeIdentityRepository(Factory);
        var userRepo = new UserRepository(Factory);
        var mediaRepo = new MediaRepository(Factory, scopeHolder);
        var folderRepo = new FolderRepository(Factory, scopeHolder);
        var versionRepo = new ArticleVersionRepository(Factory, scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(Factory, scopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo,
            new FakeEmbeddingGenerator(), new NullEventLogger());

        FolderRepo = folderRepo;
        Session = new SessionService(keySlotRepo);
        InitService = new InitializationService(nodeRepo, keySlotRepo, userRepo, Factory);
        ArticleService = new ArticleService(articleRepo, bodyRepo, Session, nodeRepo,
            new NullLamportClock(), new NullEventLogger(), mediaRepo, folderRepo,
            versionRepo, new NullActorProvider(), conceptTagService, Factory);

        TempMediaDir = Path.Combine(Path.GetTempPath(), $"bmb_test_media_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempMediaDir);
        MediaService = new MediaService(mediaRepo, articleRepo, Session, nodeRepo,
            new NullLamportClock(), new NullEventLogger(),
            new MediaStorageOptions(TempMediaDir), Factory);

        ImportService = new BeeImportService(ArticleService, MediaService, folderRepo, nodeRepo);

        await InitService.InitializeAsync("admin", "TestNode", "password");
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

    private static Stream BuildBeeZip(
        BeeExportManifest manifest,
        IEnumerable<(string path, string content)> mdEntries,
        IEnumerable<(string path, byte[] data)>? imageEntries = null)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry(".bmb-manifest.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
                writer.Write(JsonSerializer.Serialize(manifest, ManifestJsonOpts));

            foreach (var (path, content) in mdEntries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }

            foreach (var (path, data) in imageEntries ?? [])
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static byte[] CreateMinimalPng()
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1, 1);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task MissingManifest_Throws()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("note.md", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("hello");
        }
        ms.Position = 0;

        var act = () => ImportService.ImportAsync(ms, "/", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*doesn't look like a BeeMemoryBank export*");
    }

    [Fact]
    public async Task ExactTitleAndTags_RestoredVerbatim_NotDerivedFromFilename()
    {
        var manifest = new BeeExportManifest
        {
            SourceFolderName = "Мой гитхаб",
            Folders = [""],
            Articles =
            [
                new BeeExportManifestArticle
                {
                    File = "00 — Процесс аудита (SOP).md",
                    Title = "00 — Процесс аудита (SOP): «специальные» символы!",
                    Tags = ["Process", "github-review"],
                    Protected = false
                }
            ]
        };
        using var zip = BuildBeeZip(manifest,
            [("00 — Процесс аудита (SOP).md", "Body text.")]);

        var report = await ImportService.ImportAsync(zip, "/Work", CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        report.RootFolderPath.Should().Be("/Work/Мой гитхаб");

        var articles = await ArticleService.ListAsync("/Work/Мой гитхаб");
        articles.Should().ContainSingle();
        articles[0].Title.Should().Be("00 — Процесс аудита (SOP): «специальные» символы!");

        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Be("Body text.");
    }

    [Fact]
    public async Task EmptyFolder_SurvivesTheRoundTrip()
    {
        var manifest = new BeeExportManifest
        {
            SourceFolderName = "Мой гитхаб",
            Folders = ["", "Аудиты", "Аудиты/2026-07-19"],
            Articles = []
        };
        using var zip = BuildBeeZip(manifest, []);

        var report = await ImportService.ImportAsync(zip, "/", CancellationToken.None);

        report.FoldersCreated.Should().Be(3);
        (await FolderRepo.GetByPathAsync("/Мой гитхаб")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Мой гитхаб/Аудиты")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Мой гитхаб/Аудиты/2026-07-19")).Should().NotBeNull();
    }

    [Fact]
    public async Task NoSourceFolderName_ImportsDirectlyIntoDestination_NoWrapperFolder()
    {
        var manifest = new BeeExportManifest
        {
            SourceFolderName = null,
            Folders = [],
            Articles = [new BeeExportManifestArticle { File = "note.md", Title = "Note", Tags = [] }]
        };
        using var zip = BuildBeeZip(manifest, [("note.md", "Body.")]);

        var report = await ImportService.ImportAsync(zip, "/Work", CancellationToken.None);

        report.RootFolderPath.Should().Be("/Work");
        var articles = await ArticleService.ListAsync("/Work");
        articles.Should().ContainSingle(a => a.TreePath == "/Work");
    }

    [Fact]
    public async Task DestinationNameCollision_ResolvedWithSuffix()
    {
        // Pre-existing folder at the destination with the SAME name the manifest wants to use.
        await ArticleService.CreateAsync("Placeholder", "/Work/Мой гитхаб", [], "x");

        var manifest = new BeeExportManifest
        {
            SourceFolderName = "Мой гитхаб",
            Folders = [""],
            Articles = [new BeeExportManifestArticle { File = "note.md", Title = "Note", Tags = [] }]
        };
        using var zip = BuildBeeZip(manifest, [("note.md", "Body.")]);

        var report = await ImportService.ImportAsync(zip, "/Work", CancellationToken.None);

        report.RootFolderPath.Should().Be("/Work/Мой гитхаб (2)");
    }

    [Fact]
    public async Task ProtectedArticle_SkippedWithWarning_NotImportedAsPlaceholder()
    {
        var manifest = new BeeExportManifest
        {
            SourceFolderName = "X",
            Folders = [""],
            Articles =
            [
                new BeeExportManifestArticle { File = "secret.md", Title = "Secret", Tags = [], Protected = true },
                new BeeExportManifestArticle { File = "open.md", Title = "Open", Tags = [], Protected = false }
            ]
        };
        using var zip = BuildBeeZip(manifest,
            [("secret.md", "🔒 This article is password-protected..."), ("open.md", "plain body")]);

        var report = await ImportService.ImportAsync(zip, "/", CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        report.ArticlesSkippedProtected.Should().Be(1);
        report.Warnings.Should().ContainSingle(w => w.Contains("Secret") && w.Contains("password-protected"));

        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        articles.Should().ContainSingle();
        articles[0].Title.Should().Be("Open");
    }

    [Fact]
    public async Task ImageReference_RewrittenToMediaRef()
    {
        var png = CreateMinimalPng();
        var manifest = new BeeExportManifest
        {
            SourceFolderName = "X",
            Folders = [""],
            Articles = [new BeeExportManifestArticle { File = "note.md", Title = "Note", Tags = [] }]
        };
        using var zip = BuildBeeZip(manifest,
            [("note.md", "See ![pic](attachments/pic.png) here.")],
            [("attachments/pic.png", png)]);

        var report = await ImportService.ImportAsync(zip, "/", CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        report.ImagesImported.Should().Be(1);

        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Match("See ![pic](/api/media/*) here.");
        content.Should().NotContain("attachments/pic.png");
    }

    [Fact]
    public async Task SubfolderStructure_Preserved()
    {
        var manifest = new BeeExportManifest
        {
            SourceFolderName = "X",
            Folders = ["", "Notes", "Notes/projects"],
            Articles =
            [
                new BeeExportManifestArticle { File = "Notes/daily.md", Title = "Daily", Tags = [] },
                new BeeExportManifestArticle { File = "Notes/projects/alpha.md", Title = "Alpha", Tags = [] },
                new BeeExportManifestArticle { File = "ideas.md", Title = "Ideas", Tags = [] }
            ]
        };
        using var zip = BuildBeeZip(manifest,
            [("Notes/daily.md", "Daily"), ("Notes/projects/alpha.md", "Alpha"), ("ideas.md", "Ideas")]);

        var report = await ImportService.ImportAsync(zip, "/", CancellationToken.None);

        report.ArticlesCreated.Should().Be(3);
        (await ArticleService.ListAsync(report.RootFolderPath + "/Notes"))
            .Should().ContainSingle(a => a.TreePath == report.RootFolderPath + "/Notes" && a.Title == "Daily");
        (await ArticleService.ListAsync(report.RootFolderPath + "/Notes/projects"))
            .Should().ContainSingle(a => a.TreePath == report.RootFolderPath + "/Notes/projects" && a.Title == "Alpha");
        (await ArticleService.ListAsync(report.RootFolderPath))
            .Should().ContainSingle(a => a.TreePath == report.RootFolderPath && a.Title == "Ideas");
    }
}
