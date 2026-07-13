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
