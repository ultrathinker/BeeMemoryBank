using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Snapshot restore (<c>POST /api/snapshots/restore</c>) has to keep the vault UNLOCKED across the
/// work and lock it afterwards. The handler used to lock first, which broke both halves of the
/// operation in ways nothing here covered:
///
/// <list type="bullet">
/// <item><description><c>SnapshotService.CreateAsync</c> encrypts the snapshot database only while
/// the session is unlocked, so the optional pre-restore safety backup — a full copy of the vault —
/// was written to disk in the clear.</description></item>
/// <item><description><c>RestoreAsync</c> → <c>DecryptDbIfNeededAsync</c> throws outright on an
/// encrypted snapshot when the session is locked, and <c>CreateAsync</c> encrypts by default — so
/// restoring an encrypted snapshot could not succeed at all.</description></item>
/// </list>
///
/// Locking afterwards is still required: the restore replaces the database file, and the master DEK
/// held in memory belongs to the vault that was just swapped out.
/// </summary>
public class SnapshotRestoreSessionTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "snapshotRestorePassword";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ASnapshotTakenWhileUnlocked_IsEncrypted()
    {
        var resp = await _client.PostAsync("/api/snapshots", null);
        resp.EnsureSuccessStatusCode();
        var fileName = (await resp.Content.ReadFromJsonAsync<SnapshotCreatedDto>())!.FileName;

        // Pins the premise the restore behaviour rests on: the default snapshot IS encrypted, so a
        // restore path that cannot decrypt is a restore path that cannot run at all.
        (await ContainsEncryptedDbAsync(SnapshotPath(fileName))).Should().BeTrue(
            "CreateAsync encrypts the snapshot database by default when the session is unlocked");
    }

    [Fact]
    public async Task RestoringAnEncryptedSnapshot_Succeeds_AndLocksTheSessionAfterwards()
    {
        await CreateArticleAsync("before-snapshot");
        var fileName = await CreateFullSnapshotAsync();
        (await ContainsEncryptedDbAsync(SnapshotPath(fileName))).Should().BeTrue();

        var resp = await RestoreAsync(fileName, Password, createBackupFirst: false);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Services.GetRequiredService<SessionService>().IsUnlocked
            .Should().BeFalse("the restored database may be sealed with a different master DEK");
    }

    [Fact]
    public async Task TheOptionalPreRestoreBackup_IsAlsoEncrypted()
    {
        await CreateArticleAsync("before-snapshot");
        var fileName = await CreateFullSnapshotAsync();
        var before = Directory.GetFiles(SnapshotsDir, "*.tar.gz").ToHashSet();

        var resp = await RestoreAsync(fileName, Password, createBackupFirst: true);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var backup = Directory.GetFiles(SnapshotsDir, "*.tar.gz").Except(before).ToList();
        backup.Should().ContainSingle("the restore was asked to take a backup first");

        // The backup is a complete copy of the vault. Writing it in the clear because the session
        // was locked a moment too early is a data-at-rest downgrade that leaves no visible symptom.
        (await ContainsEncryptedDbAsync(backup[0])).Should().BeTrue();
    }

    [Fact]
    public async Task RestoreWithAWrongPassword_IsRefused_AndLeavesTheSessionAsItWas()
    {
        var fileName = await CreateFullSnapshotAsync();

        var resp = await RestoreAsync(fileName, "not-the-password", createBackupFirst: false);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Services.GetRequiredService<SessionService>().IsUnlocked
            .Should().BeTrue("a rejected restore must not disturb the session it never touched");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string SnapshotsDir => _factory.Services.GetRequiredService<SnapshotService>().SnapshotsDir;

    private string SnapshotPath(string fileName) => Path.Combine(SnapshotsDir, fileName);

    private Task<HttpResponseMessage> RestoreAsync(string fileName, string password, bool createBackupFirst)
        => _client.PostAsJsonAsync("/api/snapshots/restore", new
        {
            fileName,
            masterPassword = password,
            createBackupFirst,
            standaloneMode = true
        });

    private async Task CreateArticleAsync(string title)
    {
        var resp = await _client.PostAsJsonAsync("/api/articles",
            new { title, content = "body", treePath = "/" });
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a snapshot with <c>filterSecrets: false</c> — a complete backup, which is the shape
    /// restore is built to consume. The default snapshot (what <c>POST /api/snapshots</c> produces)
    /// DROPS the secret tables, and restoring one of those fails on the first
    /// <c>DELETE FROM tbl_sync_position</c> against a table that is no longer there. That is a
    /// separate, pre-existing defect in the create/restore pair, not what these tests are about;
    /// going through the service directly keeps the encryption behaviour under test from depending
    /// on it.
    /// </summary>
    private async Task<string> CreateFullSnapshotAsync()
    {
        var info = await _factory.Services.GetRequiredService<SnapshotService>()
            .CreateAsync(filterSecrets: false);
        return info.FileName;
    }

    /// <summary>
    /// True when the snapshot's embedded database carries the encryption magic header. Reads the
    /// tar.gz rather than trusting an API field, so the assertion is about the bytes on disk.
    /// </summary>
    private static async Task<bool> ContainsEncryptedDbAsync(string snapshotPath)
    {
        await using var file = File.OpenRead(snapshotPath);
        await using var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gz);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (!entry.Name.EndsWith("beememorybank.db", StringComparison.OrdinalIgnoreCase)) continue;
            using var ms = new MemoryStream();
            entry.DataStream!.CopyTo(ms);
            var head = ms.ToArray().AsSpan(0, Math.Min(6, (int)ms.Length));
            return System.Text.Encoding.ASCII.GetString(head).StartsWith("BMBDB", StringComparison.Ordinal);
        }
        return false;
    }

    private sealed record SnapshotCreatedDto(string FileName);
}
