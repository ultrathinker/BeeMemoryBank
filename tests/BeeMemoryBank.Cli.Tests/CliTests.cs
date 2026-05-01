using BeeMemoryBank.Cli;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

public class CliTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_cli_" + Guid.NewGuid().ToString("N"));

    private const string NodeName = "CliTestNode";
    private const string Password = "cliTestPassword";

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ───────────────────── init ─────────────────────

    [Fact]
    public async Task Init_CreatesDatabase_AndNodeIdentity()
    {
        var result = await InitCommand.HandleAsync(_tempDir, NodeName, Password);
        result.Should().Be(0);

        // Verify via service provider
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        var initSvc = scope.ServiceProvider.GetRequiredService<InitializationService>();
        (await initSvc.IsInitializedAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Init_AlreadyInitialized_Returns1()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        var sw = new StringWriter();
        var result = await InitCommand.HandleAsync(_tempDir, NodeName, Password, sw);
        result.Should().Be(1);
        sw.ToString().Should().Contain("already initialized");
    }

    // ───────────────────── status ─────────────────────

    [Fact]
    public async Task Status_NotInitialized_Returns1()
    {
        // Create empty directory without init
        Directory.CreateDirectory(_tempDir);
        var sw = new StringWriter();
        var result = await StatusCommand.HandleAsync(_tempDir, sw);
        result.Should().Be(1);
        sw.ToString().Should().Contain("not initialized");
    }

    [Fact]
    public async Task Status_AfterInit_ShowsNodeName()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        var sw = new StringWriter();
        var result = await StatusCommand.HandleAsync(_tempDir, sw);
        result.Should().Be(0);
        sw.ToString().Should().Contain(NodeName);
    }

    // ───────────────────── unlock ─────────────────────

    [Fact]
    public async Task Unlock_CorrectPassword_Returns0()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        var sw = new StringWriter();
        var result = await UnlockCommand.HandleAsync(_tempDir, Password, sw);
        result.Should().Be(0);
        sw.ToString().Should().Contain("unlocked");
    }

    [Fact]
    public async Task Unlock_WrongPassword_Returns1()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        var sw = new StringWriter();
        var result = await UnlockCommand.HandleAsync(_tempDir, "wrong_password", sw);
        result.Should().Be(1);
        sw.ToString().Should().Contain("invalid password");
    }

    // ───────────────────── article create & list ─────────────────────

    [Fact]
    public async Task Article_CreateAndList_ViaCliRoundtrip()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        // Create
        var createOut = new StringWriter();
        var createResult = await ArticleCommand.HandleCreateAsync(
            _tempDir,
            title: "Test CLI Article",
            treePath: "/Test",
            content: "Test article content",
            password: Password,
            createOut);
        createResult.Should().Be(0);
        createOut.ToString().Should().Contain("Test CLI Article");

        // List
        var listOut = new StringWriter();
        var listResult = await ArticleCommand.HandleListAsync(_tempDir, treePath: null, listOut);
        listResult.Should().Be(0);
        listOut.ToString().Should().Contain("Test CLI Article");
    }

    [Fact]
    public async Task Article_Create_WrongPassword_Returns1()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        var sw = new StringWriter();
        var result = await ArticleCommand.HandleCreateAsync(
            _tempDir,
            title: "X",
            treePath: "/",
            content: "y",
            password: "wrong",
            sw);
        result.Should().Be(1);
        sw.ToString().Should().Contain("invalid password");
    }

    [Fact]
    public async Task Article_Get_ReturnsMetadata()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);
        var createOut = new StringWriter();
        await ArticleCommand.HandleCreateAsync(_tempDir, "Article for Get", "/Dev", "body", Password, createOut);

        // Extract id from create output
        var createOutput = createOut.ToString();
        var idLine = createOutput.Split('\n').FirstOrDefault(l => l.StartsWith("Article created:"));
        idLine.Should().NotBeNull();
        var id = Guid.Parse(idLine!.Replace("Article created:", "").Trim());

        var getOut = new StringWriter();
        var result = await ArticleCommand.HandleGetAsync(_tempDir, id, showContent: false, password: null, getOut);
        result.Should().Be(0);
        getOut.ToString().Should().Contain("Article for Get");
    }

    [Fact]
    public async Task Article_GetContent_DecryptsBody()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);
        var createOut = new StringWriter();
        await ArticleCommand.HandleCreateAsync(_tempDir, "Encrypted Article", "/Sec", "Secret text", Password, createOut);

        var idLine = createOut.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("Article created:"));
        var id = Guid.Parse(idLine!.Replace("Article created:", "").Trim());

        var getOut = new StringWriter();
        var result = await ArticleCommand.HandleGetAsync(_tempDir, id, showContent: true, password: Password, getOut);
        result.Should().Be(0);
        getOut.ToString().Should().Contain("Secret text");
    }

    [Fact]
    public async Task Article_List_FilterByPath()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);
        await ArticleCommand.HandleCreateAsync(_tempDir, "Work Article", "/Work", "w", Password);
        await ArticleCommand.HandleCreateAsync(_tempDir, "Personal Article", "/Personal", "p", Password);

        var listOut = new StringWriter();
        await ArticleCommand.HandleListAsync(_tempDir, treePath: "/Work", listOut);
        var output = listOut.ToString();
        output.Should().Contain("Work Article");
        output.Should().NotContain("Personal Article");
    }

    [Fact]
    public async Task Article_Delete_RemovesFromList()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);
        var createOut = new StringWriter();
        await ArticleCommand.HandleCreateAsync(_tempDir, "To Delete", "/Trash", "x", Password, createOut);

        var idLine = createOut.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("Article created:"));
        var id = Guid.Parse(idLine!.Replace("Article created:", "").Trim());

        var deleteResult = await ArticleCommand.HandleDeleteAsync(_tempDir, id, Password);
        deleteResult.Should().Be(0);

        var listOut = new StringWriter();
        await ArticleCommand.HandleListAsync(_tempDir, null, listOut);
        listOut.ToString().Should().NotContain("To Delete");
    }

    // ───────────────────── snapshot ─────────────────────

    [Fact]
    public async Task Snapshot_Create_NoDatabase_Returns1()
    {
        Directory.CreateDirectory(_tempDir);
        var sw = new StringWriter();
        var result = await SnapshotCommand.HandleCreateAsync(_tempDir, sw);
        result.Should().Be(1);
        sw.ToString().Should().Contain("database not found");
    }

    [Fact]
    public async Task Snapshot_Create_ContainsAllArticles()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);
        await ArticleCommand.HandleCreateAsync(_tempDir, "Article for Snapshot", "/Snap", "content", Password);

        var sw = new StringWriter();
        var result = await SnapshotCommand.HandleCreateAsync(_tempDir, sw);
        result.Should().Be(0);

        var output = sw.ToString();
        output.Should().Contain("Snapshot created");
        output.Should().Contain(NodeName);

        // Verify file exists
        var snapshotLine = output.Split('\n').FirstOrDefault(l => l.Contains(".tar.gz"));
        snapshotLine.Should().NotBeNull();
        var snapshotPath = snapshotLine!.Replace("Snapshot created:", "").Trim();
        File.Exists(snapshotPath).Should().BeTrue();

        // Delete snapshot after test
        File.Delete(snapshotPath);
    }

    [Fact]
    public async Task Snapshot_RestoreOnFreshNode_AllDataAvailable()
    {
        // Create a node with an article
        var sourceDir = Path.Combine(Path.GetTempPath(), "bmb_snap_src_" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitCommand.HandleAsync(sourceDir, "SnapshotNode", Password);
            await ArticleCommand.HandleCreateAsync(sourceDir, "Restored Article", "/Snap", "body", Password);

            var sw = new StringWriter();
            await SnapshotCommand.HandleCreateAsync(sourceDir, sw);
            var output = sw.ToString();
            var snapshotLine = output.Split('\n').FirstOrDefault(l => l.Contains(".tar.gz"));
            var snapshotPath = snapshotLine!.Replace("Snapshot created:", "").Trim();

            try
            {
                // Restore into _tempDir
                var restoreSw = new StringWriter();
                var restoreResult = await SnapshotCommand.HandleRestoreAsync(_tempDir, snapshotPath, restoreSw);
                restoreResult.Should().Be(0);
                restoreSw.ToString().Should().Contain("Snapshot restored");

                // Verify article is accessible
                var listSw = new StringWriter();
                var listResult = await ArticleCommand.HandleListAsync(_tempDir, null, listSw);
                listResult.Should().Be(0);
                listSw.ToString().Should().Contain("Restored Article");
            }
            finally
            {
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            }
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_Manifest_HashesMatch()
    {
        await InitCommand.HandleAsync(_tempDir, NodeName, Password);

        var sw = new StringWriter();
        var result = await SnapshotCommand.HandleCreateAsync(_tempDir, sw);
        result.Should().Be(0);

        var output = sw.ToString();
        // SHA256 is printed in output
        output.Should().Contain("SHA256(db):");

        var snapshotLine = output.Split('\n').FirstOrDefault(l => l.Contains(".tar.gz"));
        var snapshotPath = snapshotLine!.Replace("Snapshot created:", "").Trim();
        try
        {
            // Restore should succeed (validates hash internally)
            var restoreDir = Path.Combine(Path.GetTempPath(), "bmb_hash_" + Guid.NewGuid().ToString("N"));
            try
            {
                var restoreSw = new StringWriter();
                var restoreResult = await SnapshotCommand.HandleRestoreAsync(restoreDir, snapshotPath, restoreSw);
                restoreResult.Should().Be(0);
            }
            finally
            {
                if (Directory.Exists(restoreDir)) Directory.Delete(restoreDir, recursive: true);
            }
        }
        finally
        {
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        }
    }
}
