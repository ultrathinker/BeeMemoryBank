using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The round trip nothing covered: create a snapshot the way the Admin UI does, then restore it
/// the way the Admin UI does, and get the articles back.
///
/// Both halves were tested in isolation and both passed, while the pair was broken for two months.
/// <c>CreateAsync</c>'s <c>filterSecrets</c> parameter defaults to <c>true</c> — a package for a
/// joining peer, with tbl_user, tbl_key_slot and tbl_node_identity DROPPED — and the create
/// endpoint was the one call site that relied on the default. Every snapshot the UI produced was
/// therefore unrestorable, failing deep inside restore on a <c>DELETE</c> against a table that was
/// no longer there. The restore tests sidestepped it by calling the service with an explicit
/// <c>filterSecrets: false</c>, which is precisely the argument the endpoint was missing.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class SnapshotRoundTripTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "snapshotRoundTripPassword";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeEncryptedNodeAsync(password: Password);
        await UnlockAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ArticlesComeBack_AfterASnapshotAndRestoreThroughTheApi()
    {
        await CreateArticleAsync("in-the-snapshot");
        var fileName = await CreateSnapshotThroughTheApiAsync();
        await CreateArticleAsync("written-after-the-snapshot");

        var restore = await RestoreAsync(fileName);
        restore.StatusCode.Should().Be(HttpStatusCode.OK, await restore.Content.ReadAsStringAsync());

        // The restore locks the vault, and the key slots now come from the archive. The same master
        // password still opens them — that is what makes this a backup rather than a one-way trip.
        (await UnlockAsync()).StatusCode.Should().Be(HttpStatusCode.OK,
            "the restored key slots were wrapped with this very password");

        var titles = await ListArticleTitlesAsync();
        titles.Should().Contain("in-the-snapshot");
        titles.Should().NotContain("written-after-the-snapshot",
            "restore replaces the vault with the archived state");
    }

    [Fact]
    public async Task ASnapshotCreatedThroughTheApi_CarriesTheAccountsThatCanOpenIt()
    {
        var fileName = await CreateSnapshotThroughTheApiAsync();

        var tables = await ReadArchiveTableNamesAsync(SnapshotPath(fileName));

        // Named individually rather than as "not filtered": each one is load-bearing on its own.
        // tbl_key_slot holds the only wrapped copies of the master DEK, so an archive without it is
        // ciphertext no password can ever open again.
        tables.Should().Contain("tbl_key_slot");
        tables.Should().Contain("tbl_user");
        tables.Should().Contain("tbl_node_identity");
    }

    [Fact]
    public async Task RestoringAPeerJoinPackage_IsRefusedWithAnExplanation()
    {
        // What POST /api/snapshots used to hand out, and what a peer legitimately receives from
        // /api/sync/snapshot/for-join. Restoring it would swap in a database with no key slots.
        var info = await _factory.Services.GetRequiredService<SnapshotService>()
            .CreateAsync(filterSecrets: true);
        await CreateArticleAsync("must-survive-the-refusal");

        var resp = await RestoreAsync(info.FileName);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("tbl_key_slot", "the operator needs to know what is missing");

        (await UnlockAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListArticleTitlesAsync()).Should().Contain("must-survive-the-refusal",
            "the refusal happens before anything on disk is replaced");
    }

    // ---- BMB-314: backups across the squash ----------------------------------------------------------------------------------------

    /// <summary>Gives the live database the ledger a node of an older release has (the rows of the old chain, as many as asked).</summary>
    private async Task ForgeLedgerAsync(int rows)
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("DROP TRIGGER IF EXISTS trg_migration_ledger_guard");
        await conn.ExecuteAsync("DELETE FROM tbl_migration");
        foreach (var (version, name) in LegacyMigrationChain.LedgerRows.Take(rows))
            await conn.ExecuteAsync("INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (@version, @name, @at, @at)",
                new { version, name, at = DateTime.UtcNow.ToString("o") });
    }

    [Fact]
    public async Task ABackupMadeByA261Node_RestoresIntoThisVersion_AndIsAdoptedAtTheNextStart()
    {
        // The backup's database has the 39-row ledger of 2.6.1. Restoring replaces the files; the runner of this version takes the database
        // over when the node starts, so the next start is simulated by running it on the restored file.
        await ForgeLedgerAsync(39);
        await CreateArticleAsync("in-the-261-backup");
        var fileName = await CreateSnapshotThroughTheApiAsync();
        await CreateArticleAsync("written-after-the-backup");

        var restore = await RestoreAsync(fileName);
        restore.StatusCode.Should().Be(HttpStatusCode.OK, await restore.Content.ReadAsStringAsync());
        (await UnlockAsync()).StatusCode.Should().Be(HttpStatusCode.OK);

        var factory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        await new MigrationRunner(factory).RunMigrationsAsync();
        using (var conn = factory.CreateConnection())
            (await conn.QueryAsync<(int, string)>("SELECT version, filename FROM tbl_migration")).Should().Equal(
                [(MigrationRunner.BaselineVersion, MigrationRunner.BaselineResourceName)], "the restored 2.6.1 ledger became the baseline's");
        var titles = await ListArticleTitlesAsync();
        titles.Should().Contain("in-the-261-backup").And.NotContain("written-after-the-backup");
    }

    [Fact]
    public async Task ABackupMadeByAReleaseBefore26_IsRefusedBeforeAnythingIsReplaced()
    {
        await ForgeLedgerAsync(35); // the ledger of 2.3.0 .. 2.5.2: this is what the backup's database will carry
        var fileName = await CreateSnapshotThroughTheApiAsync();
        await ForgeLedgerAsync(39); // the live node is a 2.6.x one again (an older ledger would be refused at the next unlock, which is right)
        await CreateArticleAsync("must-survive-the-refusal");

        var resp = await RestoreAsync(fileName);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("before 2.6").And.Contain("2.6.1", "the operator needs to know what to do");
        (await UnlockAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListArticleTitlesAsync()).Should().Contain("must-survive-the-refusal", "the refusal happens before anything on disk is replaced");
    }

    /// <summary>Runs SQL on the live database: a defect of structure in something these tests never read, so that the snapshot made next carries it.</summary>
    private async Task BreakTheLiveDatabaseAsync(string sql)
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    [Fact]
    public async Task ABackupOfA261NodeWhoseStructureIsBroken_IsRefusedBeforeAnythingIsReplaced()
    {
        // Its ledger is the 39 rows of 2.6.1 (adopted at the next start), but a column the program reads is gone: the node would be replaced
        // and then refuse to start. The restore asks the adoption's own structural question of the staged copy and says no.
        await ForgeLedgerAsync(39);
        await BreakTheLiveDatabaseAsync("ALTER TABLE tbl_hard_delete_audit DROP COLUMN entity_title");
        var fileName = await CreateSnapshotThroughTheApiAsync();
        await BreakTheLiveDatabaseAsync("ALTER TABLE tbl_hard_delete_audit ADD COLUMN entity_title TEXT"); // the live node is whole again, only the backup is not
        await CreateArticleAsync("must-survive-the-refusal");

        var resp = await RestoreAsync(fileName);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("tbl_hard_delete_audit.entity_title").And.Contain("2.6.1", "the operator learns what is wrong and what to do");
        (await UnlockAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListArticleTitlesAsync()).Should().Contain("must-survive-the-refusal", "the refusal happens before anything on disk is replaced");
    }

    [Fact]
    public async Task ABackupWithTheBaselineLedgerThatNeedsARepair_IsRefusedBeforeAnythingIsReplaced()
    {
        // The live node carries the baseline's ledger, so does the backup; nothing repairs a missing index of such a database at the next
        // start (only an adoption repairs), so the restore must not install it.
        await BreakTheLiveDatabaseAsync("DROP INDEX idx_hard_delete_audit_occurred_at");
        var fileName = await CreateSnapshotThroughTheApiAsync();
        await CreateArticleAsync("must-survive-the-refusal");

        var resp = await RestoreAsync(fileName);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("idx_hard_delete_audit_occurred_at");
        (await UnlockAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListArticleTitlesAsync()).Should().Contain("must-survive-the-refusal", "the refusal happens before anything on disk is replaced");
    }

    [Fact]
    public async Task CreatingAnEncryptedSnapshotWhileLocked_IsRefusedRatherThanWrittenInTheClear()
    {
        _factory.Services.GetRequiredService<SessionService>().Lock();

        var create = async () => await _factory.Services.GetRequiredService<SnapshotService>()
            .CreateAsync(filterSecrets: false);

        // The old behaviour was to notice the vault was locked and quietly write the database
        // unencrypted under the same file name — a full plaintext copy of every article, produced
        // exactly when nobody is watching (right after a restart, or from a scheduled job).
        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*encrypted and closed*");
        Directory.GetFiles(SnapshotsDir, "*.tar.gz").Should().BeEmpty();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string SnapshotsDir => _factory.Services.GetRequiredService<SnapshotService>().SnapshotsDir;

    private string SnapshotPath(string fileName) => Path.Combine(SnapshotsDir, fileName);

    private Task<HttpResponseMessage> UnlockAsync()
        => _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

    /// <summary>Creates a snapshot exactly as Admin → Snapshots does — no service shortcuts.</summary>
    private async Task<string> CreateSnapshotThroughTheApiAsync()
    {
        var resp = await _client.PostAsync("/api/snapshots", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SnapshotCreatedDto>())!.FileName;
    }

    private Task<HttpResponseMessage> RestoreAsync(string fileName)
        => _client.PostAsJsonAsync("/api/snapshots/restore", new
        {
            fileName,
            masterPassword = Password,
            createBackupFirst = false,
            standaloneMode = true
        });

    private async Task CreateArticleAsync(string title)
    {
        var resp = await _client.PostAsJsonAsync("/api/articles",
            new { title, content = "body", treePath = "/" });
        resp.EnsureSuccessStatusCode();
    }

    private async Task<List<string>> ListArticleTitlesAsync()
    {
        var resp = await _client.GetAsync("/api/articles");
        resp.EnsureSuccessStatusCode();
        var articles = await resp.Content.ReadFromJsonAsync<JsonElement[]>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return articles!.Select(a => a.GetProperty("title").GetString()!).ToList();
    }

    /// <summary>
    /// Table names inside the archive's database, read from the bytes on disk rather than from an
    /// API field — the question is what a future restore would actually find in there.
    /// </summary>
    private async Task<HashSet<string>> ReadArchiveTableNamesAsync(string snapshotPath)
    {
        var extracted = Path.Combine(Path.GetTempPath(), $"bmb-roundtrip-{Guid.NewGuid():N}.db");
        try
        {
            await using (var file = File.OpenRead(snapshotPath))
            await using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var tar = new System.Formats.Tar.TarReader(gz))
            {
                while (await tar.GetNextEntryAsync() is { } entry)
                {
                    if (!entry.Name.EndsWith("beememorybank.db", StringComparison.OrdinalIgnoreCase)) continue;
                    await using var outFile = File.Create(extracted);
                    await entry.DataStream!.CopyToAsync(outFile);
                    break;
                }
            }
            File.Exists(extracted).Should().BeTrue("the snapshot must contain a database");

            // Snapshots are encrypted at rest; decrypt in place, the same way restore does.
            await _factory.Services.GetRequiredService<SnapshotService>()
                .DecryptDbIfNeededAsync(extracted);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // On an encrypted node the opened archive is a staging copy under the node's own page key: opened the way the node opens its files.
            using var conn = _factory.Services.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>().OpenFile(extracted);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(extracted)) File.Delete(extracted);
        }
    }

    private sealed record SnapshotCreatedDto(string FileName);
}
