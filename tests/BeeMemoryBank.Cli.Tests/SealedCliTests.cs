using System.Reflection;
using System.Security.Cryptography;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// The command line on an encrypted folder (docs/design/step-b-spec.md 5.9, 8.7.2 step 4; red tests 15.3.12 and 15.3.14): <c>bmb status</c> needs no
/// password, the verbs that read the vault ask for it (<c>--password-stdin</c> or a prompt) or say how, and <c>bmb vault verify</c> names the check
/// that failed. The folder is a real node: initialised through the CLI, converted by the page codec, with a boot store written by the real mirror.
/// </summary>
[Collection("CliSealedStatics")]
public sealed class SealedCliTests : IDisposable
{
    private const string Password = "AdminPass1x";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_cli_sealed_" + Guid.NewGuid().ToString("N"));
    private readonly string _canary = MetadataCanaries.Ascii("title");

    public SealedCliTests()
    {
        NodeApi.UrlOverride = "http://127.0.0.1:1";   // no node answers: a test must not reach a node of the machine it runs on
        CliSealed.Stdin = new ThrowingReader();
        CliSealed.Error = new StringWriter();
        CliSealed.StdinIsRedirected = () => true;
        CliSealed.Set(null, false);
    }

    public void Dispose()
    {
        NodeApi.UrlOverride = null;
        CliSealed.Stdin = Console.In;
        CliSealed.Error = Console.Error;
        CliSealed.StdinIsRedirected = () => Console.IsInputRedirected;
        CliSealed.Set(null, false);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadLine() => throw new InvalidOperationException("The command must not read stdin here.");
    }

    private static void GivePassword(string password)
    {
        CliSealed.Stdin = new StringReader(password + "\n");
        CliSealed.Set(null, fromStdin: true);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    /// <summary>A real node's folder, sealed: one article with a canary title, a recovery key, the boot store of the migration.</summary>
    private async Task BuildSealedAsync()
    {
        (await PlainNode.InitAsync(_dir, "admin", Password)).Should().Be(0);
        (await ArticleCommand.HandleCreateAsync(_dir, _canary, "/Work", "body text", Password)).Should().Be(0);
        byte[] dek;
        await using (var services = await CliServiceProvider.CreateAsync(_dir))
        {
            using var scope = services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            (await session.UnlockAsync(Password)).Should().BeTrue();
            await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
            dek = session.GetMasterDek();
            await AwaitPostUnlockCatchUps(session);
        }
        SqliteConnection.ClearAllPools();

        var vaultPath = Path_("beememorybank.db");
        var bootPath = Path_("beememorybank.boot.db");
        var boot = new BootStore(bootPath);
        var doors = new BootDoorStore(boot.Connections);
        var wrappers = new BootDbKeyStore(boot.Connections);
        var keyService = new DatabaseKeyService(boot, wrappers);
        var dbk = keyService.Create();
        var vaultKey = keyService.DerivePageKey(dbk, DbPurposes.Vault);
        var chatKey = keyService.DerivePageKey(dbk, DbPurposes.Chat);
        try
        {
            var sealedPath = vaultPath + ".sealed-tmp";
            await using (var image = SwapStep(() => File.OpenRead(vaultPath)))
                await PlainImageImporter.ToSealedFileAsync(image, image.Length, sealedPath, vaultKey, memoryCap: 256L << 20);
            foreach (var sidecar in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(vaultPath + sidecar)) SwapStep(() => File.Delete(vaultPath + sidecar));
            SwapStep(() => File.Delete(vaultPath));
            SwapStep(() => File.Move(sealedPath, vaultPath));

            using var sealedVault = new DbConnectionFactory(vaultPath, new InMemoryVaultKeySource(vaultKey, chatKey));
            var mirror = new UnlockMaterialMirror(sealedVault, boot, doors, wrappers, new BootStoreWriter(boot.Connections));
            await mirror.WriteNewBootStoreAsync(bootPath, sealedVault, dbk, 1, [dek]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dbk);
            CryptographicOperations.ZeroMemory(vaultKey);
            CryptographicOperations.ZeroMemory(chatKey);
            CryptographicOperations.ZeroMemory(dek);
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Waits out the fire-and-forget post-unlock catch-ups (DEK-rotation drain, legacy comment sealing, pending-restore retries,
    /// recovery reconciliation, the legacy node-key migration). <c>UnlockAsync</c> detaches them as <c>Task.Run</c> and returns
    /// immediately; each one opens its own scope and reads the vault database, and under load the sweep can still be running when
    /// the hand-seal below deletes <c>beememorybank.db</c> - the checked-out connection then holds the file and the delete dies
    /// with an IOException. Integration projects holding an InternalsVisibleTo just await <c>SessionService.PostUnlockCatchUp</c>;
    /// this project has no grant, so the same awaitable is read by reflection, failing loudly if it moves rather than silently
    /// skipping the wait.
    /// </summary>
    private static async Task AwaitPostUnlockCatchUps(SessionService session)
    {
        var awaitable = typeof(SessionService).GetProperty("PostUnlockCatchUp", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SessionService.PostUnlockCatchUp is gone - update this helper.");
        await (Task)(awaitable.GetValue(session) ?? throw new InvalidOperationException("SessionService.PostUnlockCatchUp was null."));
    }

    /// <summary>Runs one file operation of the seal swap with a short bounded retry on IOException: just after the connection pool
    /// has let go of a vault file, Windows can still answer a delete or move with a sharing violation for a moment (a scanner or the
    /// indexer opening the freshly written file). The budget is about 2 s; past it the last exception propagates, so a real holder
    /// of the file still fails the test, with its own error.</summary>
    private static void SwapStep(Action step)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { step(); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
        }
    }

    private static T SwapStep<T>(Func<T> step)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return step(); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
        }
    }

    // ---------------------------------------------------------------- 15.3.12

    [Fact]
    public async Task Status_of_an_encrypted_folder_needs_no_password_and_reads_nothing_from_stdin()
    {
        await BuildSealedAsync();
        var output = new StringWriter();

        var exit = await StatusCommand.HandleAsync(_dir, output);

        exit.Should().Be(0, output.ToString());
        var text = output.ToString();
        text.Should().Contain("encrypted").And.Contain("closed").And.Contain("Node:");
        text.Should().NotContain(_canary, "the database is closed: no title is shown");
    }

    [Fact]
    public async Task Article_list_refuses_an_encrypted_folder_without_the_password_and_works_with_password_stdin()
    {
        await BuildSealedAsync();
        var output = new StringWriter();

        // No password given: an empty pipe on stdin (the password is read as a line when stdin is a pipe, as for init and join).
        CliSealed.Stdin = new StringReader("");
        var refused = async () => await ArticleCommand.HandleListAsync(_dir, null, output);
        (await refused.Should().ThrowAsync<CliVaultException>()).Which.Message.Should().Contain("encrypted").And.Contain("--password-stdin");

        CliSealed.Stdin = new StringReader("not the password\n");
        CliSealed.Set(null, fromStdin: true);
        var wrong = async () => await ArticleCommand.HandleListAsync(_dir, null, output);
        (await wrong.Should().ThrowAsync<CliVaultException>()).Which.Message.Should().Contain("invalid password");

        GivePassword(Password);
        (await ArticleCommand.HandleListAsync(_dir, null, output)).Should().Be(0, output.ToString());
        output.ToString().Should().Contain(_canary);
    }

    [Fact]
    public async Task Article_list_takes_the_password_of_the_command_line_too()
    {
        await BuildSealedAsync();
        var output = new StringWriter();
        CliSealed.Capture(["article", "get", Guid.NewGuid().ToString(), "--password", Password]);

        (await ArticleCommand.HandleListAsync(_dir, null, output)).Should().Be(0, output.ToString());
        output.ToString().Should().Contain(_canary);
    }

    [Fact]
    public async Task A_folder_that_cannot_start_is_refused_by_name_by_status_and_by_every_verb()
    {
        await BuildSealedAsync();
        var plainChat = Path_("chat.db");
        using (var c = new SqliteConnection($"Data Source={plainChat};Pooling=False"))
        {
            c.Open();
            c.Execute("CREATE TABLE chat_message (id INTEGER PRIMARY KEY, body TEXT); INSERT INTO chat_message (body) VALUES ('a plain chat')");
        }
        SqliteConnection.ClearAllPools();
        var status = new StringWriter();

        (await StatusCommand.HandleAsync(_dir, status)).Should().Be(1);
        status.ToString().Should().Contain("plain_companion").And.Contain("chat.db");

        GivePassword(Password);
        var refused = async () => await ArticleCommand.HandleListAsync(_dir, null, new StringWriter());
        (await refused.Should().ThrowAsync<CliVaultException>()).Which.Message.Should().Contain("chat.db").And.Contain("Nothing was opened");
    }

    // ---------------------------------------------------------------- 15.3.14

    [Fact]
    public async Task Verify_says_ok_on_a_good_set_and_needs_the_password()
    {
        await BuildSealedAsync();
        var output = new StringWriter();

        CliSealed.Stdin = new StringReader("");
        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(1);
        output.ToString().Should().Contain("master password");

        output = new StringWriter();
        GivePassword(Password);
        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(0, output.ToString());
        output.ToString().Trim().Should().Be("ok");
        CliSealed.IsSealed(_dir).Should().BeTrue();
        using (var s = new FileStream(Path_("beememorybank.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) s.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Verify_names_a_wrong_password()
    {
        await BuildSealedAsync();
        var output = new StringWriter();
        GivePassword("not the password");

        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(1);
        output.ToString().Should().Contain("failed: password");
    }

    [Fact]
    public async Task Verify_names_a_damaged_page()
    {
        await BuildSealedAsync();
        var vault = Path_("beememorybank.db");
        using (var fs = new FileStream(vault, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Length.Should().BeGreaterThan(4096 * 6, "the corrupted page is in the middle of the file");
            fs.Position = 4096 * 4 + 200;
            var b = fs.ReadByte();
            fs.Position = 4096 * 4 + 200;
            fs.WriteByte((byte)(b ^ 0xFF));
        }
        var output = new StringWriter();
        GivePassword(Password);

        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(1);
        output.ToString().Should().Contain("failed: vault_integrity");
    }

    [Fact]
    public async Task Verify_names_a_door_the_boot_store_lost()
    {
        await BuildSealedAsync();
        using (var c = new BootStore(Path_("beememorybank.boot.db")).Connections.Open())
            c.Execute("DELETE FROM tbl_boot_door WHERE slot_type = 'recovery'");
        var output = new StringWriter();
        GivePassword(Password);

        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(1);
        output.ToString().Should().Contain("failed: door").And.Contain("recovery");
    }

    [Fact]
    public async Task Verify_names_a_missing_wrapper()
    {
        await BuildSealedAsync();
        using (var c = new BootStore(Path_("beememorybank.boot.db")).Connections.Open())
            c.Execute("DELETE FROM tbl_boot_db_key");
        var output = new StringWriter();
        GivePassword(Password);

        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(1);
        output.ToString().Should().Contain("failed: wrapper");
    }

    [Fact]
    public async Task Verify_on_a_plain_folder_says_so()
    {
        (await PlainNode.InitAsync(_dir, "admin", Password)).Should().Be(0);
        var output = new StringWriter();

        (await VaultCommand.VerifyAsync(_dir, output)).Should().Be(1);
        output.ToString().Should().Contain("not_sealed");
    }
}
