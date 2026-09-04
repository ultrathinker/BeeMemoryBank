using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
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
        await _factory.InitializeNodeAsync(password: Password);
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
            .WithMessage("*locked*");
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
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={extracted};Pooling=False");
            conn.Open();
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
