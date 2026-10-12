using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A stopped, activated full node with the canary vault (step-a-spec 16.1), built once for the migration tests: "Encrypt this node" needs the new sync
/// format on, which only an activated node has. Copies of <see cref="PlainDir"/> are what each test starts.
/// </summary>
public sealed class ActivatedPlainTemplate : IAsyncLifetime
{
    public const string Password = StepANode.Password;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-activated-template-" + Guid.NewGuid().ToString("N"));
    public string PlainDir => Path.Combine(Root, "plain");
    public List<string> Canaries { get; private set; } = [];

    public async Task InitializeAsync()
    {
        await using var node = await StepANode.CreateAsync("MigrationNode");
        await StepANode.ActivateAsync(node);
        Canaries = (await StepANode.SeedWithCanariesAsync(node)).Canaries;
        using (var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        var chat = Path.Combine(node.DataPath, "chat.db");
        if (File.Exists(chat))
        {
            using var c = new SqliteConnection($"Data Source={chat};Pooling=False");
            c.Open();
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();
        SealedNode.CopyTree(node.DataPath, PlainDir, skipDatabases: false);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// "Encrypt this node" end to end on the canary vault (docs/design/step-b-spec.md 10, red tests 15.3.7, 15.3.8, 15.3.9): the product's own Api over a plain
/// folder, the migration run by its service, the process "killed" after every step by a hook that throws where a dead process would have stopped, and a
/// new host started over what is left. The disk, not the process, is what is judged.
/// </summary>
public sealed class SealMigrationApiTests(ActivatedPlainTemplate template) : IClassFixture<ActivatedPlainTemplate>
{
    private const string Password = ActivatedPlainTemplate.Password;

    /// <summary>Every step of 10.4 and of the swap of 10.6 after which a process may die, in the order they happen on a node with a chat database.</summary>
    private static readonly string[] Steps =
    [
        "preflight", "checkpoint", "journal-building", "snapshot", "keys", "vacuum-vault", "vacuum-chat", "boot-target", "verified-check",
        "journal-verified", "journal-swapping",
        "wiped-old-beememorybank.db", "old-aside-beememorybank.db", "renamed-beememorybank.db",
        "wiped-old-chat.db", "old-aside-chat.db", "renamed-chat.db",
        "renamed-beememorybank.boot.db",
        "wiped-beememorybank.db.seal-old", "wiped-chat.db.seal-old", "wiped", "journal-deleted",
        "swapped", "final-scan", "opened", "audit",
    ];

    /// <summary>From this step on the journal says "verified": a start goes forward (sealed); before it, the node is plain with its old files.</summary>
    private static bool SealedAfter(string step) => Array.IndexOf(Steps, step) >= Array.IndexOf(Steps, "journal-verified");

    private async Task<SealedApiNode> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_seal_run_" + Guid.NewGuid().ToString("N"));
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        var node = SealedApiNode.PlainOver(dir, configure);
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        return node;
    }

    // ---------------------------------------------------------------- the whole run

    [Fact]
    public async Task The_migration_encrypts_the_canary_node_and_no_byte_of_it_is_left_in_clear()
    {
        using var node = await StartAsync();
        var before = await DumpAsync(node);
        Scan(node.Dir, template.Canaries).Should().NotBeEmpty("the control: a plain node shows its canaries to a raw scan");

        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var preflight = await seal.PreflightAsync(new SealOptions());
        preflight.Blockers.Should().BeEmpty("the activated canary node may be encrypted: " + string.Join(", ", preflight.Blockers.Select(b => b.Code)));

        var copies = RecordWayBackCopies(node);
        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Done, result.Error);
        result.Snapshot.Should().NotBeNull("the way back was written");
        copies.Should().NotBeEmpty("the way-back snapshot made its working copy");
        node.Gate.IsSealed.Should().BeTrue();
        node.Gate.State.Should().Be(VaultState.Open, "the session's key opens the new database without a door");
        NoCanaryAndNoLeftover(node.Dir, template.Canaries);
        result.PlainCopiesFound.Should().BeEmpty();
        using (var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'vault_sealed'").Should().Be(1, "the audit entry has no names");

        node.Restart();
        node.Gate.IsSealed.Should().BeTrue();
        node.Gate.State.Should().Be(VaultState.Closed, "a sealed node starts closed");
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before, "the data after the unlock is the data before the migration");
        var dek = node.Session.GetMasterDek();
        (await SnapshotOpensAsync(Directory.EnumerateFiles(Path.Combine(node.Dir, "pre-encryption"), "bmb-snapshot-*.tar.gz").Single(), dek)).Should().BeTrue();
        // After the restart the node's own background work may hold a staging copy of a baseline (made once the vault opens): a sealed copy in
        // progress, which is not a leftover. What must be gone is the copy of the way-back snapshot; no canary may be in the folder.
        WayBackCopiesGone(copies);
        NoLeftover(node.Dir);
        Scan(node.Dir, template.Canaries).Should().BeEmpty("a raw scan of every file of the data folder finds no canary");
    }

    // ---------------------------------------------------------------- 15.3.7 kill points

    [Fact]
    public async Task The_list_of_kill_points_is_the_list_of_steps_a_migration_reports()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var seen = new List<string>();
        seal.StepHook = seen.Add;

        (await seal.RunAsync(Password, new SealOptions())).Phase.Should().Be(SealProgress.Done);

        // SQLite's -wal and -shm of a replaced database exist or not by what was still open: their wipes are steps only when there is something to wipe.
        seen.RemoveAll(s => s.EndsWith("-wal", StringComparison.Ordinal) || s.EndsWith("-shm", StringComparison.Ordinal));
        var at = Enumerable.Range(0, Math.Max(seen.Count, Steps.Length)).FirstOrDefault(i => i >= seen.Count || i >= Steps.Length || seen[i] != Steps[i], -1);
        at.Should().Be(-1, "a step added or renamed in the migration needs a kill point here; the first difference: it reported "
            + (at >= 0 && at < seen.Count ? seen[at] : "(nothing more)") + " where the list has " + (at >= 0 && at < Steps.Length ? Steps[at] : "(nothing more)")
            + " [it reported: " + string.Join(", ", seen) + "]");
    }

    [Theory]
    [MemberData(nameof(KillPoints))]
    public async Task A_process_killed_after_a_step_restarts_plain_with_its_old_files_or_sealed_with_the_new_ones_and_never_leaves_a_plain_copy(string step)
    {
        using var node = await StartAsync();
        var before = await DumpAsync(node);
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        seal.StepHook = s => { if (s == step) throw new SealKill(s); };

        var kill = async () => await seal.RunAsync(Password, new SealOptions());
        await kill.Should().ThrowAsync<SealKill>();

        node.Restart();
        var sealedNow = SealedAfter(step);
        node.Gate.IsSealed.Should().Be(sealedNow, $"after {step} the start goes {(sealedNow ? "forward" : "back")}");
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before, "no third state: the data is the source's");

        NoLeftover(node.Dir);
        if (sealedNow) Scan(node.Dir, template.Canaries).Should().BeEmpty("no plain copy survives the next start");
        foreach (var archive in SnapshotsIn(node.Dir))
            (await SnapshotOpensAsync(archive, node.Session.GetMasterDek())).Should().BeTrue("the way back decrypts with the key at every kill point");
        if (step is "snapshot" or "keys" or "vacuum-vault" or "journal-verified" or "audit")
            SnapshotsIn(node.Dir).Should().NotBeEmpty("a snapshot written before the first change stays");
    }

    public static IEnumerable<object[]> KillPoints() => Steps.Select(s => new object[] { s });

    // ---- review round 1, H1: no Done and no open while a readable plain file is left ----------------------------------------------------

    [Fact]
    public async Task A_wipe_that_fails_fails_the_run_closed_and_no_unlock_opens_the_node_while_the_readable_file_is_there()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        seal.WipeOverride = _ => false;   // a locked file, and it stays locked

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Failed, "a readable plain database on the disk is not a finished encryption");
        result.ErrorCode.Should().Be(SealFailures.Wipe);
        result.Error.Should().NotContain("remove it yourself", "the owner is never asked to delete a readable file");
        node.Gate.IsSealed.Should().BeTrue("the swap is done: the live pair is the encrypted one");
        node.Gate.State.Should().Be(VaultState.Closed, "the vault was not opened over the leftover");
        node.Session.IsUnlocked.Should().BeFalse("the key of the session is gone with the failed run");
        var (journalState, journal) = SealJournal.Read(node.Dir);
        journalState.Should().Be(SealJournalState.Valid);
        journal!.Phase.Should().Be(SealJournal.Swapping, "the retry rides on the journal");
        SealSwapResolver.Leftovers(node.Dir).Should().Contain("beememorybank.db.seal-old");
        using (var client = node.Client())
        {
            var status = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/vault/status");
            status.GetProperty("migration").GetProperty("wipe_pending").EnumerateArray().Select(e => e.GetString()).Should().Contain("beememorybank.db.seal-old");
        }

        (await node.Session.UnlockAsync(Password)).Should().BeFalse("no unlock while the replaced plain database is still readable");
        node.Gate.State.Should().Be(VaultState.Closed);

        node.Restart();

        NoLeftover(node.Dir);
        node.Gate.IsSealed.Should().BeTrue();
        (await node.Session.UnlockAsync(Password)).Should().BeTrue("the start wiped the leftover, so the node opens");
        Scan(node.Dir, template.Canaries).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unlock_after_a_failed_wipe_retries_the_wipe_and_opens_once_the_file_is_gone()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        seal.WipeOverride = _ => false;
        (await seal.RunAsync(Password, new SealOptions())).ErrorCode.Should().Be(SealFailures.Wipe);
        (await node.Session.UnlockAsync(Password)).Should().BeFalse();

        seal.WipeOverride = null;   // the lock is lifted
        var unlocked = await node.Session.UnlockAsync(Password);

        unlocked.Should().BeTrue("the unlock tried the wipe again and it went through");
        NoLeftover(node.Dir);
        node.Gate.State.Should().Be(VaultState.Open);
    }

    [Fact]
    public async Task A_wipe_that_goes_through_on_a_retry_ends_the_run_in_Done_with_nothing_left()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var tries = new Dictionary<string, int>();
        seal.WipeOverride = path =>
        {
            tries[path] = tries.GetValueOrDefault(path) + 1;
            return tries[path] >= 3 && SecureFileWipe.TryWipe(path);   // the third try goes through
        };

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Done, result.Error);
        NoCanaryAndNoLeftover(node.Dir, template.Canaries);
        node.Gate.State.Should().Be(VaultState.Open);
    }

    [Fact]
    public async Task A_plain_copy_that_the_final_scan_finds_is_erased_before_the_node_opens()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var copy = Path.Combine(node.Dir, "vault-copy.db");
        seal.StepHook = step => { if (step == "swapped") File.Copy(Path.Combine(template.PlainDir, VaultStoreFiles.Database), copy); };   // appears during the run

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Done, result.Error);
        File.Exists(copy).Should().BeFalse("a plain copy of the vault is erased by the run, the owner is not asked to");
        result.PlainCopiesFound.Should().BeNullOrEmpty();
        NoCanaryAndNoLeftover(node.Dir, template.Canaries);
    }

    [Fact]
    public async Task A_plain_copy_that_cannot_be_erased_fails_the_run_closed_and_is_named()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var copy = Path.Combine(node.Dir, "vault-copy.db");
        seal.StepHook = step => { if (step == "swapped") File.Copy(Path.Combine(template.PlainDir, VaultStoreFiles.Database), copy); };
        seal.WipeOverride = path => !path.EndsWith("vault-copy.db", StringComparison.Ordinal) && SecureFileWipe.TryWipe(path);   // this one is locked

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Failed);
        result.ErrorCode.Should().Be(SealFailures.PlainCopies);
        result.PlainCopiesFound.Should().Contain("vault-copy.db");
        node.Gate.State.Should().NotBe(VaultState.Open, "the vault was not opened over a readable copy");
        node.Session.IsUnlocked.Should().BeFalse();
        SealSwapResolver.Leftovers(node.Dir).Should().BeEmpty("the replaced files are gone, the copy is what is left");
    }

    [Fact]
    public async Task A_hard_linked_leftover_is_unlinked_not_overwritten_and_named_in_the_status()
    {
        using var node = await StartAsync();
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var other = Path.Combine(Path.GetTempPath(), "bmb_linked_" + Guid.NewGuid().ToString("N") + ".bin");
        seal.StepHook = s =>
        {
            if (s != "old-aside-beememorybank.db") return;
            TestLinks.CreateHardLink(Path.Combine(node.Dir, "beememorybank.db.seal-old"), other);   // the owner's own second name for the plain file
            throw new SealKill(s);
        };
        var kill = async () => await seal.RunAsync(Password, new SealOptions());
        await kill.Should().ThrowAsync<SealKill>();
        var plainBytes = File.ReadAllBytes(other);

        node.Restart();

        NoLeftover(node.Dir);
        File.ReadAllBytes(other).Should().Equal(plainBytes, "the bytes of a file with another name are never overwritten");
        using var client = node.Client();
        var status = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/vault/status");
        status.GetProperty("migration").GetProperty("linked_plain").EnumerateArray().Select(e => e.GetString()).Should().Contain("beememorybank.db.seal-old");
        File.Delete(other);
    }

    // ---------------------------------------------------------------- 15.3.8 verification catches

    [Theory]
    [InlineData("row_removed", "verify_counts")]
    [InlineData("sequence_lowered", "verify_counts")]
    [InlineData("page_corrupted", "verify_")]
    [InlineData("title_in_clear", "verify_scan")]
    [InlineData("wrong_boot_key", "verify_boot")]
    public async Task Verification_catches_a_bad_copy_and_the_node_is_unchanged(string fault, string expectedCode)
    {
        var dbk = RandomNumberGenerator.GetBytes(32);
        using var node = await StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDatabaseKeyService>(sp => new KnownKeyService(
                new DatabaseKeyService(sp.GetRequiredService<IBootStore>(), sp.GetRequiredService<IBootDbKeyStore>()), dbk)));
            if (fault == "wrong_boot_key")
                services.Replace(ServiceDescriptor.Singleton<IUnlockMaterialMirror>(sp => new WrongKeyMirror(sp.GetRequiredService<UnlockMaterialMirror>())));
        });
        var before = await DumpAsync(node);
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var keyService = node.Services.GetRequiredService<IDatabaseKeyService>();
        seal.StepHook = step =>
        {
            if (step != "vacuum-vault" || fault == "wrong_boot_key") return;
            var target = Path.Combine(node.Dir, "beememorybank.db.seal-new");
            var key = keyService.DerivePageKey(dbk, DbPurposes.Vault);
            switch (fault)
            {
                case "row_removed":
                    // A row of a table nothing references (an article row has children, and removing one would fail the reference check first).
                    using (var c = SealedSqlite.OpenKeyed(target, key)) c.Execute("DELETE FROM tbl_event WHERE sequence_num = (SELECT MIN(sequence_num) FROM tbl_event)");
                    break;
                case "sequence_lowered":
                    using (var c = SealedSqlite.OpenKeyed(target, key)) c.Execute("UPDATE sqlite_sequence SET seq = seq - 1 WHERE name = 'tbl_event'");
                    break;
                case "page_corrupted":
                    using (var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        fs.Position = 4096 * 3 + 100;
                        var b = fs.ReadByte();
                        fs.Position = 4096 * 3 + 100;
                        fs.WriteByte((byte)(b ^ 0xFF));
                    }
                    break;
                case "title_in_clear":
                    using (var fs = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.None))
                        foreach (var canary in template.Canaries) fs.Write(System.Text.Encoding.UTF8.GetBytes(canary));
                    break;
            }
        };

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Failed);
        result.ErrorCode.Should().StartWith(expectedCode);
        Unchanged(node, before);
    }

    [Fact]
    public async Task A_way_back_snapshot_that_fails_its_self_check_stops_the_run_with_the_code_snapshot_and_changes_nothing()
    {
        using var node = await StartAsync();
        var before = await DumpAsync(node);
        var seal = node.Services.GetRequiredService<SealMigrationService>();

        // The envelope of the snapshot has to open with the password the run is given: a different one fails the check.
        var result = await seal.RunAsync("a password that is not the master password", new SealOptions());

        result.Phase.Should().Be(SealProgress.Failed);
        result.ErrorCode.Should().Be(SealFailures.Snapshot);
        SnapshotsIn(node.Dir).Should().BeEmpty("a snapshot that failed its check is removed");
        Unchanged(node, before);
    }

    [Fact]
    public async Task The_way_back_copy_leaves_no_SQLite_sidecar_behind_and_the_migration_is_done()
    {
        using var node = await StartAsync();
        node.Services.GetRequiredService<SnapshotService>().AfterStagingWork = PlantClearSidecars;
        var seal = node.Services.GetRequiredService<SealMigrationService>();

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Done, result.Error);
        result.PlainCopiesFound.Should().BeEmpty();
        NoCanaryAndNoLeftover(node.Dir, template.Canaries);
        Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(node.Dir)).Should().BeEmpty("the way-back copy and its sidecars are gone");
    }

    [Fact]
    public async Task A_way_back_copy_whose_sidecar_cannot_be_removed_fails_the_migration_and_changes_nothing()
    {
        using var node = await StartAsync();
        var before = await DumpAsync(node);
        node.Services.GetRequiredService<SnapshotService>().AfterStagingWork = copy => Directory.CreateDirectory(copy + "-journal");
        var seal = node.Services.GetRequiredService<SealMigrationService>();

        var result = await seal.RunAsync(Password, new SealOptions());

        result.Phase.Should().Be(SealProgress.Failed, "a clear copy that stays is a failure, never Done");
        result.ErrorCode.Should().Be(SealFailures.Snapshot);
        SnapshotsIn(node.Dir).Should().BeEmpty();
        Unchanged(node, before);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Records the working copies the snapshot service makes (the way-back snapshot is the only one a migration makes).</summary>
    private static List<string> RecordWayBackCopies(SealedApiNode node)
    {
        var copies = new List<string>();
        node.Services.GetRequiredService<SnapshotService>().AfterStagingWork = copies.Add;
        return copies;
    }

    /// <summary>The recorded copies and each of their SQLite sidecars are gone.</summary>
    private static void WayBackCopiesGone(List<string> copies) =>
        copies.SelectMany(copy => new[] { copy, copy + "-journal", copy + "-wal", copy + "-shm" })
            .Where(file => File.Exists(file) || Directory.Exists(file))
            .Should().BeEmpty("the way-back copy and its SQLite sidecars are removed");

    /// <summary>What SQLite leaves beside a working copy of a plain vault: the same kind of bytes it would hold.</summary>
    private static void PlantClearSidecars(string copy)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) File.WriteAllText(copy + suffix, "clear vault pages");
    }

    private void Unchanged(SealedApiNode node, Dictionary<string, string> before)
    {
        node.Gate.IsSealed.Should().BeFalse();
        NoLeftover(node.Dir);
        Directory.EnumerateFiles(node.Dir, "*.seal-new*").Should().BeEmpty("the failed targets are gone");
        File.Exists(Path.Combine(node.Dir, VaultStoreFiles.SealJournal)).Should().BeFalse("the journal went with them");
        node.Session.IsUnlocked.Should().BeTrue();
        DumpAsync(node).GetAwaiter().GetResult().Should().BeEquivalentTo(before);
    }

    private static void NoLeftover(string dir)
    {
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains(".seal-old", StringComparison.Ordinal) || f.EndsWith(".unreadable", StringComparison.Ordinal))
            .Should().BeEmpty("no replaced plain file survives a start");
        File.Exists(Path.Combine(dir, VaultStoreFiles.SealJournal)).Should().BeFalse();
    }

    /// <summary>
    /// The raw scan of the folder of a node that is running: the node's own background work (a baseline in the staging folder) may delete a file between
    /// the listing and the read, which is not a finding. The scan is repeated; what is still there on the last try is judged.
    /// </summary>
    private static List<CanaryHit> Scan(string dir, IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        for (var attempt = 1; ; attempt++)
        {
            try { return MetadataCanaries.ScanDirectory(dir, list); }
            catch (Exception ex) when (attempt < 8 && ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void NoCanaryAndNoLeftover(string dir, IEnumerable<string> canaries)
    {
        NoLeftover(dir);
        Scan(dir, canaries).Should().BeEmpty("a raw scan of every file of the data folder finds no canary");
        PlainCopyScan.Run(dir, PlainScanMode.Sealed).Should().BeEmpty();   // the product's scan: a running node's own lock file is not a finding
    }

    private static List<string> SnapshotsIn(string dir)
    {
        var folder = Path.Combine(dir, VaultStoreFiles.PreEncryptionDir);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "bmb-snapshot-*.tar.gz").ToList() : [];
    }

    /// <summary>The snapshot's database container decrypts with <paramref name="dek"/> to a whole SQLite image.</summary>
    private static async Task<bool> SnapshotOpensAsync(string archive, byte[] dek)
    {
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
        {
            if (entry.Name != "beememorybank.db" || entry.DataStream is null) continue;
            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer);
            var image = await Bmbdb2Container.DecryptAsync(new MemoryStream(buffer.ToArray()), buffer.Length, dek);
            return image.Length > 100 && image.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8);
        }
        return false;
    }

    /// <summary>A hash per table of what the owner would call the vault, and the position of the log.</summary>
    private static readonly HashSet<string> Derived = new(["embedding_projection", "embedding_model_version", "embedding_pending", "index_pending"], StringComparer.Ordinal);

    private static async Task<Dictionary<string, string>> DumpAsync(SealedApiNode node)
    {
        var vault = node.Services.GetRequiredService<DbConnectionFactory>();
        using var c = vault.CreateConnection();
        var result = new Dictionary<string, string>();
        foreach (var table in new[] { "tbl_article", "tbl_folder", "tbl_user", "tbl_key_slot", "tbl_media" })
        {
            if (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }) == 0) continue;
            using var command = c.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using var reader = command.ExecuteReader();
            var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (Derived.Contains(reader.GetName(i))) continue;   // caches the background processors write and a restore clears
                    var value = reader.GetValue(i);
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(i + ":" + value.GetType().Name + ":"));
                    hash.AppendData(value is byte[] bytes ? bytes : System.Text.Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
                }
            }
            result[table] = rows + ":" + Convert.ToHexString(hash.GetHashAndReset());
        }
        result["event_seq"] = Convert.ToString(await c.ExecuteScalarAsync<long?>("SELECT seq FROM sqlite_sequence WHERE name = 'tbl_event'")) ?? "";
        result["event_max"] = Convert.ToString(await c.ExecuteScalarAsync<long?>("SELECT MAX(sequence_num) FROM tbl_event")) ?? "";
        return result;
    }

    /// <summary>A key service that hands out one known database key, so a test can open the target the migration is making.</summary>
    private sealed class KnownKeyService(IDatabaseKeyService inner, byte[] databaseKey) : IDatabaseKeyService
    {
        public byte[] Create() => (byte[])databaseKey.Clone();

        public Task<byte[]?> TryUnwrapAsync(byte[] masterDek, CancellationToken ct = default) => inner.TryUnwrapAsync(masterDek, ct);

        public byte[] DerivePageKey(ReadOnlySpan<byte> key, string purpose) => inner.DerivePageKey(key, purpose);
    }

    /// <summary>A mirror that writes the boot store of the target with a database key that is not the one the copy was sealed with.</summary>
    private sealed class WrongKeyMirror(IUnlockMaterialMirror inner) : IUnlockMaterialMirror
    {
        public Task RewriteAsync(CancellationToken ct = default) => inner.RewriteAsync(ct);
        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) => inner.AddPendingDoorAsync(door, ct);
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => inner.RetireDoorAsync(kind, sourceId, ct);
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => inner.AddDbKeyWrapperAsync(masterDek, ct);

        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
            IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) =>
            inner.WriteNewBootStoreAsync(bootPath, vault, RandomNumberGenerator.GetBytes(32), dbkGeneration, masterDeks, ct);
    }
}
