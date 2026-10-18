using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb init</c> sets a node up encrypted (BMB-306, the owner's rule for a fresh installation): the database is encrypted under the master password
/// given, there is a boot store, no file of the setup is left in the open, metadata encryption is on, and the node starts locked. The Web Setup and
/// <c>POST /api/init/standalone</c> give the same result (<c>FreshNodeSealedSetupTests</c> in the Integration project).
/// </summary>
[Collection("CliSealedStatics")]
public sealed class InitSealedTests : IDisposable
{
    private const string Password = "InitSealedPw1x";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_cli_init_sealed_" + Guid.NewGuid().ToString("N"));

    public InitSealedTests()
    {
        NodeApi.UrlOverride = "http://127.0.0.1:1";   // no node answers: a test must not reach a node of the machine it runs on
        CliSealed.Error = new StringWriter();
        CliSealed.StdinIsRedirected = () => true;
        CliSealed.Set(null, false);
    }

    public void Dispose()
    {
        NodeApi.UrlOverride = null;
        CliSealed.Error = Console.Error;
        CliSealed.StdinIsRedirected = () => Console.IsInputRedirected;
        CliSealed.Set(null, false);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Head(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[16];
        stream.ReadExactly(bytes);
        return bytes;
    }

    [Fact]
    public async Task Init_leaves_an_encrypted_database_a_boot_store_and_no_file_of_the_setup_in_the_open()
    {
        var output = new StringWriter();

        (await InitCommand.HandleAsync(_dir, "admin", Password, output)).Should().Be(0, output.ToString());

        output.ToString().Should().Contain("successfully initialized").And.Contain("encrypted");
        VaultStoreFiles.DetectMode(_dir).Should().Be(VaultStoreMode.Sealed);
        File.Exists(Path.Combine(_dir, VaultStoreFiles.Boot)).Should().BeTrue();
        Head(Path.Combine(_dir, VaultStoreFiles.Database)).Should().NotEqual("SQLite format 3\0"u8.ToArray(), "the database is not a plain SQLite file");
        File.Exists(Path.Combine(_dir, VaultStoreFiles.SealJournal)).Should().BeFalse();
        Directory.EnumerateFiles(_dir, "*.seal-*", SearchOption.AllDirectories).Should().BeEmpty("no replaced plain file is left");
    }

    [Fact]
    public async Task The_password_of_the_init_opens_the_node_and_the_node_has_metadata_encryption_on()
    {
        (await InitCommand.HandleAsync(_dir, "admin", Password, new StringWriter())).Should().Be(0);

        CliSealed.Set(Password, false);
        await using var services = await CliServiceProvider.CreateAsync(_dir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.DisplayName.Should().Be("admin");
        using var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_metadata_activation").Should().Be(1, "the node holds the activation of its own setup");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_metadata_key").Should().BeGreaterThan(0);
        services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue("the node declares protocol 4 from its first start");
    }

    [Fact]
    public async Task A_wrong_password_does_not_open_the_node_a_command_line_init_made()
    {
        (await InitCommand.HandleAsync(_dir, "admin", Password, new StringWriter())).Should().Be(0);

        CliSealed.Set("not the password", false);
        var open = async () => await CliServiceProvider.CreateAsync(_dir);

        await open.Should().ThrowAsync<CliVaultException>();
    }

    [Fact]
    public async Task A_second_init_over_the_node_is_refused_and_changes_nothing()
    {
        (await InitCommand.HandleAsync(_dir, "admin", Password, new StringWriter())).Should().Be(0);

        CliSealed.Set(Password, false);
        var output = new StringWriter();
        (await InitCommand.HandleAsync(_dir, "other", "AnotherPw1x", output)).Should().Be(1);

        output.ToString().Should().Contain("already");
        await using var services = await CliServiceProvider.CreateAsync(_dir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.DisplayName.Should().Be("admin", "the node is the one the first init made");
    }

    /// <summary>
    /// The first command after the setup must already write sealed content: a node that holds its activation never goes on writing clear metadata, and the
    /// policy that says so is read from the open database, not guessed while it opens.
    /// </summary>
    [Fact]
    public async Task The_first_article_written_after_the_init_is_sealed_content_not_clear_metadata()
    {
        (await InitCommand.HandleAsync(_dir, "admin", Password, new StringWriter())).Should().Be(0);
        CliSealed.Set(Password, false);

        (await ArticleCommand.HandleCreateAsync(_dir, MetadataCanaries.Ascii("title"), "/Work", "body text", Password)).Should().Be(0);

        CliSealed.Set(Password, false);
        await using var services = await CliServiceProvider.CreateAsync(_dir);
        using var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var types = conn.Query<string>("SELECT event_type FROM tbl_event WHERE event_type NOT LIKE 'metadata_%' AND event_type NOT LIKE 'whitelist%'").ToList();
        types.Should().Contain("sealed", "the content of the article travels sealed from the first event");
        types.Should().NotContain(t => t.StartsWith("article", StringComparison.Ordinal), "no clear article event was written");
    }
}
