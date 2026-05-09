using System.IO.Compression;
using System.Text;
using BeeMemoryBank.Core.Models;
using SixLabors.ImageSharp;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

public class ObsidianImportServiceTests : IAsyncLifetime
{
    private DbConnectionFactory Factory { get; set; } = null!;
    private SessionService Session { get; set; } = null!;
    private InitializationService InitService { get; set; } = null!;
    private ArticleService ArticleService { get; set; } = null!;
    private MediaService MediaService { get; set; } = null!;
    private ObsidianImportService ImportService { get; set; } = null!;
    private string TempMediaDir { get; set; } = "";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        Factory = DbConnectionFactory.CreateInMemory($"bmb_obsidian_{Guid.NewGuid():N}");
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

        Session = new SessionService(keySlotRepo);
        InitService = new InitializationService(nodeRepo, keySlotRepo, userRepo, Factory);
        ArticleService = new ArticleService(articleRepo, bodyRepo, Session, nodeRepo,
            new NullLamportClock(), new NullEventLogger(), mediaRepo, folderRepo,
            versionRepo, new NullActorProvider(), conceptTagService);

        TempMediaDir = Path.Combine(Path.GetTempPath(), $"bmb_test_media_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempMediaDir);
        MediaService = new MediaService(mediaRepo, articleRepo, Session, nodeRepo,
            new NullLamportClock(), new NullEventLogger(),
            new MediaStorageOptions(TempMediaDir));

        ImportService = new ObsidianImportService(ArticleService, MediaService);

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

    private static Stream BuildZip(params (string path, string content)[] textEntries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in textEntries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static Stream BuildZipWithImage(
        IEnumerable<(string path, string content)> textEntries,
        IEnumerable<(string path, byte[] data)> imageEntries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in textEntries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
            foreach (var (path, data) in imageEntries)
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
    public async Task SingleMd_NoFrontmatter_UsesFilenameAsTitle()
    {
        using var zip = BuildZip(("notes.md", "Hello world"));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        report.RootFolderPath.Should().StartWith("/Imported from Obsidian (");

        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        articles.Should().HaveCount(1);
        articles[0].Title.Should().Be("notes");

        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Be("Hello world");
    }

    [Fact]
    public async Task MdWithFrontmatter_StripsAndExtractsTitle()
    {
        var md = "---\ntitle: My Note\ndate: 2024-01-01\n---\nBody text here.";
        using var zip = BuildZip(("note.md", md));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        articles[0].Title.Should().Be("My Note");

        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Be("Body text here.");
    }

    [Fact]
    public async Task ObsidianImageEmbed_RewrittenToMediaRef()
    {
        var png = CreateMinimalPng();
        var md = "Here is an image: ![[pic.png]]";
        using var zip = BuildZipWithImage(
            [("note.md", md)],
            [("pic.png", png)]);

        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        report.ImagesImported.Should().Be(1);

        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Match("Here is an image: ![pic](/api/media/*)");
        content.Should().NotContain("![[pic.png]]");
    }

    [Fact]
    public async Task NonImageEmbed_ReplacedWithPlaceholder()
    {
        var md = "See ![[document.pdf]] for details.";
        using var zip = BuildZip(("note.md", md));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        report.FilesSkipped.Should().Be(1);

        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Contain("[файл не импортирован: document.pdf]");
    }

    [Fact]
    public async Task Wikilink_LeftUntouched()
    {
        var md = "See [[Other Article]] and [[Another|alias]] for context.";
        using var zip = BuildZip(("note.md", md));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        var content = await ArticleService.GetContentAsync(articles[0].Id);
        content.Should().Contain("[[Other Article]]");
        content.Should().Contain("[[Another|alias]]");
    }

    [Fact]
    public async Task SubfolderStructure_Preserved()
    {
        using var zip = BuildZip(
            ("vault/Notes/daily.md", "Daily"),
            ("vault/Notes/projects/alpha.md", "Alpha"),
            ("vault/ideas.md", "Ideas"));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(3);

        var dailyDirect = (await ArticleService.ListAsync(report.RootFolderPath + "/Notes"))
            .Where(a => a.TreePath == report.RootFolderPath + "/Notes").ToList();
        dailyDirect.Should().HaveCount(1);
        dailyDirect[0].Title.Should().Be("daily");

        var alphaDirect = (await ArticleService.ListAsync(report.RootFolderPath + "/Notes/projects"))
            .Where(a => a.TreePath == report.RootFolderPath + "/Notes/projects").ToList();
        alphaDirect.Should().HaveCount(1);
        alphaDirect[0].Title.Should().Be("alpha");

        var ideasDirect = (await ArticleService.ListAsync(report.RootFolderPath))
            .Where(a => a.TreePath == report.RootFolderPath).ToList();
        ideasDirect.Should().HaveCount(1);
        ideasDirect[0].Title.Should().Be("ideas");
    }

    [Fact]
    public async Task DotFilesAndObsidianDir_Skipped()
    {
        using var zip = BuildZip(
            (".obsidian/config.json", "cfg"),
            (".hidden.md", "hidden"),
            ("__MACOSX/._note.md", "mac"),
            ("note.md", "visible"));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
        var articles = await ArticleService.ListAsync(report.RootFolderPath);
        articles[0].Title.Should().Be("note");
    }

    [Fact]
    public async Task SingleTopLevelDir_Stripped()
    {
        using var zip = BuildZip(
            ("my-vault/readme.md", "README"),
            ("my-vault/notes/daily.md", "Daily"));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(2);

        var dailyDirect = (await ArticleService.ListAsync(report.RootFolderPath + "/notes"))
            .Where(a => a.TreePath == report.RootFolderPath + "/notes").ToList();
        dailyDirect.Should().HaveCount(1);
        dailyDirect[0].Title.Should().Be("daily");

        var readmeDirect = (await ArticleService.ListAsync(report.RootFolderPath))
            .Where(a => a.TreePath == report.RootFolderPath).ToList();
        readmeDirect.Should().HaveCount(1);
        readmeDirect[0].Title.Should().Be("readme");
    }

    [Fact]
    public async Task CanvasFile_Skipped()
    {
        using var zip = BuildZip(
            ("diagram.canvas", "{}"),
            ("note.md", "text"));
        var report = await ImportService.ImportAsync(zip, CancellationToken.None);

        report.ArticlesCreated.Should().Be(1);
    }
}
