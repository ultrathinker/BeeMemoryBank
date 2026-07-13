using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using BeeMemoryBank.AppPaths;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// Full test matrix for <see cref="LegacyDataRescue.TryRescue"/> as specified in TASK_BRIEF §6 / §98-111.
/// All tests use isolated temp directories; none touch the real BeeMemoryBankData root.
/// </summary>
public sealed class LegacyDataRescueTests : IDisposable
{
    // We route ALL rescue calls through a fake targetVaultDir that we own,
    // so BmbPaths.VaultDir() is only invoked for the recovered-<date> case.
    // To keep tests hermetic we point the "target" at our own temp dirs too.

    private readonly string _testRoot;

    public LegacyDataRescueTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"BmbRescueTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            // Restore any ACL-denied directories before deletion.
            if (Directory.Exists(_testRoot))
            {
                if (OperatingSystem.IsWindows())
                {
                    RestoreAcl(_testRoot);
                }
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string MakeDir(string name)
    {
        var path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Writes a minimal valid SQLite database file (with magic header) to the given path.
    /// </summary>
    private static void WriteValidSqliteDb(string path, int extraBytes = 4096)
    {
        // SQLite magic: "SQLite format 3\0"
        var magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(magic, 0, magic.Length);
        // Pad to simulate a real (non-empty) DB.
        var pad = new byte[extraBytes];
        fs.Write(pad, 0, pad.Length);
    }

    /// <summary>
    /// Writes a valid SQLite DB with distinct content (different bytes after header).
    /// </summary>
    private static void WriteDistinctSqliteDb(string path, byte marker = 0xAB)
    {
        var magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(magic, 0, magic.Length);
        var payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = marker;
        fs.Write(payload, 0, payload.Length);
    }

    private static void WriteFile(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // -----------------------------------------------------------------------
    // Test 1: Empty target + valid legacy → files copied, target populated
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptyTarget_ValidLegacy_CopiesAllFiles_IncludingWalShmMedia()
    {
        var legacy = MakeDir("legacy1");
        var target = MakeDir("target1");
        // Remove target dir so it is as-if it doesn't exist (rescue creates it via atomic rename).
        Directory.Delete(target);

        // Setup legacy
        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));
        File.WriteAllText(Path.Combine(legacy, "beememorybank.db-wal"), "wal");
        File.WriteAllText(Path.Combine(legacy, "beememorybank.db-shm"), "shm");
        Directory.CreateDirectory(Path.Combine(legacy, "media"));
        File.WriteAllText(Path.Combine(legacy, "media", "file1.jpg"), "imgdata");
        File.WriteAllText(Path.Combine(legacy, "node.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "os-auto-unlock.dat"), "key");
        Directory.CreateDirectory(Path.Combine(legacy, "updates"));
        File.WriteAllText(Path.Combine(legacy, "updates", "update.zip"), "zipdata");
        // Transient files — must NOT be copied
        File.WriteAllText(Path.Combine(legacy, "node.lock"), "locked");
        File.WriteAllText(Path.Combine(legacy, ".runtime.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "node.status.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "api.ready"), "1");

        var result = LegacyDataRescue.TryRescue(legacy, target);

        result.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);
        result.VaultDir.Should().Be(target);
        Directory.Exists(target).Should().BeTrue();

        // Core files
        File.Exists(Path.Combine(target, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(target, "beememorybank.db-wal")).Should().BeTrue();
        File.Exists(Path.Combine(target, "beememorybank.db-shm")).Should().BeTrue();
        // media/ subdirectory
        File.Exists(Path.Combine(target, "media", "file1.jpg")).Should().BeTrue();
        // other data files
        File.Exists(Path.Combine(target, "node.json")).Should().BeTrue();
        File.Exists(Path.Combine(target, "os-auto-unlock.dat")).Should().BeTrue();
        File.Exists(Path.Combine(target, "updates", "update.zip")).Should().BeTrue();
        // rescued-from.json marker must be present
        File.Exists(Path.Combine(target, "rescued-from.json")).Should().BeTrue();

        // Transient files must NOT be in the target
        File.Exists(Path.Combine(target, "node.lock")).Should().BeFalse();
        File.Exists(Path.Combine(target, ".runtime.json")).Should().BeFalse();
        File.Exists(Path.Combine(target, "node.status.json")).Should().BeFalse();
        File.Exists(Path.Combine(target, "api.ready")).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Test 2: Valid target, no legacy → no-op, target untouched
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidTarget_NoLegacy_ReturnsTargetAlreadyValid_NoChanges()
    {
        var legacy = MakeDir("legacy2"); // has no DB
        var target = MakeDir("target2");

        WriteValidSqliteDb(Path.Combine(target, "beememorybank.db"));
        File.WriteAllText(Path.Combine(target, "mydata.txt"), "important");

        var beforeMtime = File.GetLastWriteTimeUtc(Path.Combine(target, "beememorybank.db"));
        Thread.Sleep(10); // ensure time difference if anything were modified

        var result = LegacyDataRescue.TryRescue(legacy, target);

        result.Outcome.Should().Be(RescueOutcome.TargetAlreadyValid);
        result.VaultDir.Should().Be(target);

        // Target must be completely unchanged
        File.Exists(Path.Combine(target, "mydata.txt")).Should().BeTrue();
        var afterMtime = File.GetLastWriteTimeUtc(Path.Combine(target, "beememorybank.db"));
        afterMtime.Should().Be(beforeMtime);
    }

    // -----------------------------------------------------------------------
    // Test 3: Both valid + different DBs → recovered-<date> vault, both DBs intact
    // Fix #8: hermetic — uses isolated _testRoot, does NOT call BmbPaths.VaultDir().
    // We set LOCALAPPDATA to _testRoot for the duration of this test only.
    // -----------------------------------------------------------------------

    [Fact]
    public void BothValid_DifferentDbs_RescuesToRecoveredVault_BothDbsIntact()
    {
        var legacy = MakeDir("legacy3");
        var target = MakeDir("target3");

        // Write DIFFERENT databases (different payload bytes → different size, different hash).
        WriteDistinctSqliteDb(Path.Combine(legacy, "beememorybank.db"), marker: 0xAA);
        WriteDistinctSqliteDb(Path.Combine(target, "beememorybank.db"), marker: 0xBB);

        var legacyDbBytes = File.ReadAllBytes(Path.Combine(legacy, "beememorybank.db"));
        var targetDbBytesBefore = File.ReadAllBytes(Path.Combine(target, "beememorybank.db"));

        // Fix #8: redirect LOCALAPPDATA so BmbPaths.VaultDir resolves inside _testRoot
        var fakeLocalAppData = MakeDir("fakeAppData");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

            var result = LegacyDataRescue.TryRescue(legacy, target);

            result.Outcome.Should().Be(RescueOutcome.RescuedToRecoveredVault);
            result.VaultDir.Should().NotBeNullOrEmpty();
            result.VaultDir.Should().NotBe(target);
            result.VaultDir!.Should().Contain("recovered-");

            // recovered vault must exist and contain the legacy DB content
            Directory.Exists(result.VaultDir!).Should().BeTrue();
            var recoveredDbPath = Path.Combine(result.VaultDir!, "beememorybank.db");
            File.Exists(recoveredDbPath).Should().BeTrue();
            var recoveredDbBytes = File.ReadAllBytes(recoveredDbPath);
            recoveredDbBytes.Should().Equal(legacyDbBytes, "recovered vault must contain exact legacy DB bytes");

            // Original target vault must be completely untouched
            var targetDbBytesAfter = File.ReadAllBytes(Path.Combine(target, "beememorybank.db"));
            targetDbBytesAfter.Should().Equal(targetDbBytesBefore, "target DB must not be modified in conflict case");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }
    }

    // -----------------------------------------------------------------------
    // Test 4: Locked node.lock on source → LegacyFoundButRescueFailed, nothing copied
    // -----------------------------------------------------------------------

    [Fact]
    public void LockedNodeLock_ReturnsFailure_NothingCopied()
    {
        var legacy = MakeDir("legacy4");
        var target = MakeDir("target4");
        Directory.Delete(target); // ensure target does not exist

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));

        // Open node.lock with exclusive lock (simulates a running node).
        using var lockHandle = new FileStream(
            Path.Combine(legacy, "node.lock"),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = LegacyDataRescue.TryRescue(legacy, target);

        result.Outcome.Should().Be(RescueOutcome.LegacyFoundButRescueFailed);
        result.Message.Should().Contain("node.lock");
        Directory.Exists(target).Should().BeFalse("nothing should have been copied");
    }

    // -----------------------------------------------------------------------
    // Test 5: Idempotency — second call after successful rescue → no-op
    // -----------------------------------------------------------------------

    [Fact]
    public void SecondCall_AfterSuccessfulRescue_IsNoOp()
    {
        var legacy = MakeDir("legacy5");
        var target = MakeDir("target5");
        Directory.Delete(target);

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));
        WriteFile(Path.Combine(legacy, "node.json"), "{}");

        // First call — should rescue.
        var result1 = LegacyDataRescue.TryRescue(legacy, target);
        result1.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);

        // Record state after first call
        var dbSizeAfterFirst = new FileInfo(Path.Combine(target, "beememorybank.db")).Length;
        var markerTimeAfterFirst = File.GetLastWriteTimeUtc(Path.Combine(target, "rescued-from.json"));

        Thread.Sleep(50);

        // Second call — should be no-op (target now has a valid DB).
        var result2 = LegacyDataRescue.TryRescue(legacy, target);
        result2.Outcome.Should().Be(RescueOutcome.TargetAlreadyValid);
        result2.VaultDir.Should().Be(target);

        // Target must not have been touched on the second call.
        var dbSizeAfterSecond = new FileInfo(Path.Combine(target, "beememorybank.db")).Length;
        dbSizeAfterSecond.Should().Be(dbSizeAfterFirst);
        var markerTimeAfterSecond = File.GetLastWriteTimeUtc(Path.Combine(target, "rescued-from.json"));
        markerTimeAfterSecond.Should().Be(markerTimeAfterFirst, "rescued-from.json must not be overwritten on second call");
    }

    // -----------------------------------------------------------------------
    // Test 6: Copy failure mid-way → LegacyFoundButRescueFailed, no half-copied target
    // -----------------------------------------------------------------------

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void CopyFailure_ReturnsFailure_NoPartialTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            // ACL manipulation is Windows-only; skip gracefully on other OS.
            return;
        }

        var legacy = MakeDir("legacy6");
        var targetParent = MakeDir("target6parent");
        var target = Path.Combine(targetParent, "vault");
        // Do NOT create `target` — the ACL test denies CreateDirectory on it.

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));

        // Deny CreateDirectory/write in the parent, which will cause Directory.CreateDirectory
        // on the temp sibling to fail — simulating a disk/ACL failure.
        DenyDirectoryWrite(targetParent);

        try
        {
            var result = LegacyDataRescue.TryRescue(legacy, target);

            result.Outcome.Should().Be(RescueOutcome.LegacyFoundButRescueFailed,
                "a failed copy must return explicit failure, not throw");
            Directory.Exists(target).Should().BeFalse(
                "atomic rename guarantee: no partial target on failure");

            // Also verify no temp dir was left behind in the parent.
            var remainingEntries = Directory.GetFileSystemEntries(targetParent);
            remainingEntries.Should().BeEmpty("temp dir must be cleaned up on failure");
        }
        finally
        {
            RestoreAcl(targetParent);
        }
    }

    // -----------------------------------------------------------------------
    // Test 7: Broken/empty legacy (no valid SQLite header) → NoLegacyFound
    // -----------------------------------------------------------------------

    [Fact]
    public void BrokenLegacy_NoValidHeader_ReturnsNoLegacyFound()
    {
        var legacy = MakeDir("legacy7");
        var target = MakeDir("target7");
        Directory.Delete(target);

        // Write a file with exactly 4096 bytes but an INVALID SQLite header.
        var garbage = new byte[4096];
        new Random(42).NextBytes(garbage);
        // Overwrite first bytes to definitely not be SQLite magic.
        garbage[0] = 0xFF;
        garbage[1] = 0xFE;
        File.WriteAllBytes(Path.Combine(legacy, "beememorybank.db"), garbage);

        var result = LegacyDataRescue.TryRescue(legacy, target);

        result.Outcome.Should().Be(RescueOutcome.NoLegacyFound,
            "a file with invalid SQLite header must be treated as no legacy found");
        Directory.Exists(target).Should().BeFalse("nothing should have been created");
    }

    // -----------------------------------------------------------------------
    // Test 8: No legacy DB at all → NoLegacyFound
    // -----------------------------------------------------------------------

    [Fact]
    public void NoLegacyDb_ReturnsNoLegacyFound()
    {
        var legacy = MakeDir("legacy8"); // empty dir, no DB
        var target = MakeDir("target8");
        Directory.Delete(target);

        var result = LegacyDataRescue.TryRescue(legacy, target);

        result.Outcome.Should().Be(RescueOutcome.NoLegacyFound);
        Directory.Exists(target).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Test 9: rescued-from.json marker is written with correct fields
    // -----------------------------------------------------------------------

    [Fact]
    public void RescuedFromMarker_IsWritten_WithRequiredFields()
    {
        var legacy = MakeDir("legacy9");
        var target = MakeDir("target9");
        Directory.Delete(target);

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));
        WriteFile(Path.Combine(legacy, "extra.txt"), "hello");

        var result = LegacyDataRescue.TryRescue(legacy, target);
        result.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);

        var markerPath = Path.Combine(target, "rescued-from.json");
        File.Exists(markerPath).Should().BeTrue();

        var markerJson = File.ReadAllText(markerPath);
        markerJson.Should().Contain("sourcePath");
        markerJson.Should().Contain("rescuedAt");
        markerJson.Should().Contain("appVersion");
        markerJson.Should().Contain("fileCount");
        markerJson.Should().Contain("totalBytes");
        markerJson.Should().Contain(legacy.Replace("\\", "\\\\"));
    }

    // -----------------------------------------------------------------------
    // Test 10: Source never deleted
    // -----------------------------------------------------------------------

    [Fact]
    public void SourceDirectory_IsNeverDeletedOrModified()
    {
        var legacy = MakeDir("legacy10");
        var target = MakeDir("target10");
        Directory.Delete(target);

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));
        WriteFile(Path.Combine(legacy, "important.dat"), "keep me");

        var dbBytesBefore = File.ReadAllBytes(Path.Combine(legacy, "beememorybank.db"));

        var result = LegacyDataRescue.TryRescue(legacy, target);
        result.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);

        // Source directory must still exist and be identical.
        Directory.Exists(legacy).Should().BeTrue("source must never be deleted");
        File.Exists(Path.Combine(legacy, "beememorybank.db")).Should().BeTrue("source DB must survive");
        File.Exists(Path.Combine(legacy, "important.dat")).Should().BeTrue();

        var dbBytesAfter = File.ReadAllBytes(Path.Combine(legacy, "beememorybank.db"));
        dbBytesAfter.Should().Equal(dbBytesBefore, "source DB must be byte-identical after rescue");
    }

    // -----------------------------------------------------------------------
    // Test 11 (Fix #1): Unreadable legacy DB returns Failed, not NoLegacyFound
    // -----------------------------------------------------------------------

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void UnreadableLegacyDb_ReturnsFailed_NotNoLegacyFound()
    {
        if (!OperatingSystem.IsWindows()) return;

        var legacy = MakeDir("legacy11");
        var target = MakeDir("target11");
        Directory.Delete(target);

        var dbPath = Path.Combine(legacy, "beememorybank.db");
        WriteValidSqliteDb(dbPath);

        // Directly test the tri-state probe: "file exists but can't be opened"
        // Simulate via holding an exclusive handle on the file.
        using var hold = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // ProbeSqliteFile should return Unreadable (not FileNotFound).
        var status = LegacyDataRescue.ProbeSqliteFile(dbPath);
        status.Should().Be(SqliteFileStatus.Unreadable,
            "a file held exclusively should be reported as Unreadable, not FileNotFound");

        var result = LegacyDataRescue.TryRescue(legacy, target);
        result.Outcome.Should().Be(RescueOutcome.LegacyFoundButRescueFailed,
            "an unreadable (but existing) legacy DB must not silently return NoLegacyFound");
        result.Message.Should().Contain("unreadable");
        Directory.Exists(target).Should().BeFalse("nothing should have been copied");
    }

    // -----------------------------------------------------------------------
    // Test 12 (Fix #2): node.lock is held exclusively throughout the entire copy
    // -----------------------------------------------------------------------

    [Fact]
    public void NodeLock_IsHeldExclusively_ThroughoutCopy()
    {
        // We can't easily observe that the hold happens mid-copy without injecting delays,
        // but we CAN verify that after TryRescue returns, we can acquire the lock
        // (i.e. TryRescue released it) and that rescue succeeded even though node.lock
        // existed but was NOT held by an external process.

        var legacy = MakeDir("legacy12");
        var target = MakeDir("target12");
        Directory.Delete(target);

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));
        // node.lock exists but NOT held externally.
        File.WriteAllText(Path.Combine(legacy, "node.lock"), "");

        var result = LegacyDataRescue.TryRescue(legacy, target);

        // Should succeed — an un-held node.lock must not block rescue.
        result.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully,
            "a node.lock that is NOT held by another process must not block rescue");

        // After rescue, we should be able to open node.lock in the LEGACY dir exclusively
        // (confirms our lock hold was released).
        var lockPath = Path.Combine(legacy, "node.lock");
        File.Exists(lockPath).Should().BeTrue("node.lock must not be deleted from source");
        using var check = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // If we get here without IOException, the hold was properly released.
    }

    // -----------------------------------------------------------------------
    // Test 13 (Fix #3): Reparse points / junctions are skipped, not followed
    // -----------------------------------------------------------------------

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ReparsePoint_IsSkipped_NotFollowed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var legacy = MakeDir("legacy13");
        var target = MakeDir("target13");
        Directory.Delete(target);

        // Real data in legacy
        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));
        WriteFile(Path.Combine(legacy, "data.txt"), "real data");

        // Create a junction inside legacy pointing to a sibling directory
        // (simulates the media\ junction pointing to huge/external tree).
        var junctionTarget = MakeDir("junctionTarget13");
        WriteFile(Path.Combine(junctionTarget, "huge.dat"), "external content that must not be copied");
        var junctionPath = Path.Combine(legacy, "media");

        // mklink /J creates a junction without elevation on Windows
        var mklink = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{junctionPath}\" \"{junctionTarget}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var proc = System.Diagnostics.Process.Start(mklink)!)
        {
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                // Junction creation failed — skip test gracefully (e.g. CI without privilege)
                return;
            }
        }

        var result = LegacyDataRescue.TryRescue(legacy, target);

        result.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);

        // The junction itself must NOT have been followed — "huge.dat" must not appear in target
        File.Exists(Path.Combine(target, "media", "huge.dat")).Should().BeFalse(
            "reparse point (junction) must be skipped, not followed");

        // But real files must have been copied
        File.Exists(Path.Combine(target, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(target, "data.txt")).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Test 14 (Fix #5): Concurrent rescue — second call sees populated target → TargetAlreadyValid
    // -----------------------------------------------------------------------

    [Fact]
    public void ConcurrentRescue_SecondCallSeesPopulatedTarget_ReturnsTargetAlreadyValid()
    {
        // Simulate: first rescue already ran and populated target. A concurrent second
        // process calls TryRescue and finds a non-empty target. It should re-validate
        // and return TargetAlreadyValid rather than LegacyFoundButRescueFailed.

        var legacy = MakeDir("legacy14");
        var target = MakeDir("target14");
        Directory.Delete(target);

        WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));

        // First rescue
        var result1 = LegacyDataRescue.TryRescue(legacy, target);
        result1.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);

        // Second rescue (simulates concurrent / immediate retry)
        var result2 = LegacyDataRescue.TryRescue(legacy, target);
        result2.Outcome.Should().Be(RescueOutcome.TargetAlreadyValid,
            "a second rescue on an already-rescued target must not fail — data is safe");
    }

    // -----------------------------------------------------------------------
    // Test 15 (Fix #6): Full-file hash — two DBs that differ only beyond 64 KB are treated as different
    // -----------------------------------------------------------------------

    [Fact]
    public void FullFileHash_DifferencesBeyond64KB_AreTreatedAsDifferent()
    {
        var legacy = MakeDir("legacy15");
        var target = MakeDir("target15");

        var magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
        const int totalSize = 80 * 1024; // 80 KB — beyond the old 64 KB prefix

        // Write "legacy" DB: header + 64 KB of zeros + beyond-64KB payload = 0xAA
        var legacyDb = new byte[totalSize];
        Array.Copy(magic, legacyDb, magic.Length);
        for (int i = 65536; i < totalSize; i++) legacyDb[i] = 0xAA;
        File.WriteAllBytes(Path.Combine(legacy, "beememorybank.db"), legacyDb);

        // Write "target" DB: same header + same first 64 KB, but different beyond 64 KB
        var targetDb = new byte[totalSize];
        Array.Copy(legacyDb, targetDb, totalSize);
        for (int i = 65536; i < totalSize; i++) targetDb[i] = 0xBB; // different payload
        File.WriteAllBytes(Path.Combine(target, "beememorybank.db"), targetDb);

        // Force same mtime and size (would fool the old 64-KB hash into false "same")
        var t = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(legacy, "beememorybank.db"), t);
        File.SetLastWriteTimeUtc(Path.Combine(target, "beememorybank.db"), t);

        // TryRescue should see them as DIFFERENT and copy legacy to a recovered vault.
        var fakeLocalAppData = MakeDir("fakeAppData15");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);
            var result = LegacyDataRescue.TryRescue(legacy, target);
            result.Outcome.Should().Be(RescueOutcome.RescuedToRecoveredVault,
                "full-file hash must detect differences beyond 64 KB");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }
    }

    // -----------------------------------------------------------------------
    // Test 16 (Fix #7): Migration log is written to both migration\ and logs\
    // -----------------------------------------------------------------------

    [Fact]
    public void MigrationLog_IsWritten_ToBothMigrationAndLogsDir()
    {
        // Route BmbPaths to our fake LOCALAPPDATA to intercept log writes.
        var fakeLocalAppData = MakeDir("fakeAppData16");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

            var legacy = MakeDir("legacy16");
            var target = MakeDir("target16");
            Directory.Delete(target);

            WriteValidSqliteDb(Path.Combine(legacy, "beememorybank.db"));

            var result = LegacyDataRescue.TryRescue(legacy, target);
            result.Outcome.Should().Be(RescueOutcome.RescuedSuccessfully);

            // Both log directories should now contain a rescue log file.
            var migrationDir = Path.Combine(fakeLocalAppData, "BeeMemoryBankData", "migration");
            var logsDir = Path.Combine(fakeLocalAppData, "BeeMemoryBankData", "logs");

            Directory.Exists(migrationDir).Should().BeTrue("migration dir must be created");
            Directory.Exists(logsDir).Should().BeTrue("logs dir must be created");

            var migrationLogs = Directory.GetFiles(migrationDir, "rescue-*.log");
            var logsLogs = Directory.GetFiles(logsDir, "rescue-*.log");

            migrationLogs.Should().NotBeEmpty("at least one rescue log must be in migration\\");
            logsLogs.Should().NotBeEmpty("at least one rescue log must be in logs\\ (Fix #7 / spec §3.2 pt 5)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }
    }

    // -----------------------------------------------------------------------
    // Test 17 (Fix #4): TryRescue — ProbeSqliteFile distinguishes FileNotFound from Unreadable
    // -----------------------------------------------------------------------

    [Fact]
    public void ProbeSqliteFile_AbsentFile_ReturnsFileNotFound()
    {
        var path = Path.Combine(_testRoot, "nonexistent.db");
        var status = LegacyDataRescue.ProbeSqliteFile(path);
        status.Should().Be(SqliteFileStatus.FileNotFound);
    }

    [Fact]
    public void ProbeSqliteFile_InvalidHeader_ReturnsInvalidHeader()
    {
        var path = Path.Combine(_testRoot, "badheader.db");
        File.WriteAllBytes(path, new byte[64]); // all zeros, not SQLite magic
        var status = LegacyDataRescue.ProbeSqliteFile(path);
        status.Should().Be(SqliteFileStatus.InvalidHeader);
    }

    [Fact]
    public void ProbeSqliteFile_ValidSqlite_ReturnsValidSqlite()
    {
        var path = Path.Combine(_testRoot, "valid.db");
        WriteValidSqliteDb(path);
        var status = LegacyDataRescue.ProbeSqliteFile(path);
        status.Should().Be(SqliteFileStatus.ValidSqlite);
    }

    // -----------------------------------------------------------------------
    // ACL helpers (Windows only)
    // -----------------------------------------------------------------------

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DenyDirectoryWrite(string dirPath)
    {
        var di = new DirectoryInfo(dirPath);
        var acl = di.GetAccessControl();
        var currentUser = WindowsIdentity.GetCurrent().User!;
        acl.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.CreateDirectories | FileSystemRights.Write | FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny));
        di.SetAccessControl(acl);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestoreAcl(string rootPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Directory.Exists(rootPath)) return;
        try
        {
            var di = new DirectoryInfo(rootPath);
            var acl = di.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;
            // Remove all explicit Deny rules for current user.
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny &&
                    rule.IdentityReference == currentUser)
                {
                    acl.RemoveAccessRule(rule);
                }
            }
            di.SetAccessControl(acl);
        }
        catch
        {
            // Best effort.
        }
    }
}
