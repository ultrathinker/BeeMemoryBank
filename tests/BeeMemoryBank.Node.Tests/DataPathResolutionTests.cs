using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

// See NodeProcessEnvCollection (EndToEndIntegrationTests.cs) — this class mutates the process-wide
// BMB_DATA_PATH environment variable, which must not run concurrently with anything that spawns a
// real BeeMemoryBank.Node.exe subprocess.
[Collection("NodeProcessEnv")]
public class DataPathResolutionTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _dirsToDelete = new();

    public void Dispose()
    {
        // Clean up environment variable
        Environment.SetEnvironmentVariable("BMB_DATA_PATH", null);

        // Clean up created directories
        foreach (var dir in _dirsToDelete)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private string GetTempPath(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        _dirsToDelete.Add(path);
        return path;
    }

    /// <summary>
    /// Writes a minimal file with a valid SQLite magic header (what LegacyDataRescue treats as a
    /// "valid legacy database"), so we can prove the rescue is gated on the target being the
    /// DEFAULT vault — not on whether a legacy DB happens to exist.
    /// </summary>
    private static void WriteValidSqliteMagic(string path)
    {
        var magic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(magic, 0, magic.Length);
        // Pad so it looks like a non-empty DB (matches LegacyDataRescueTests' helper).
        fs.Write(new byte[4096], 0, 4096);
    }

    // ── Этап 6, §6 пункт 1.3 — rescue is a no-op for non-default vaults ─────────

    /// <summary>
    /// Fix #4 regression guard: <c>LegacyDataRescue.TryRescue</c> must NOT be invoked at all
    /// when the resolved data directory is anything other than the canonical default vault —
    /// even if a perfectly valid legacy database is sitting in <c>&lt;AppContext.BaseDirectory&gt;/data</c>.
    /// An explicit second profile's vault (or any portable/alternate install path) represents a
    /// deliberate operator choice and must never be silently mutated by the rescue.
    /// </summary>
    [Fact]
    public async Task RunOrchestratorAsync_NonDefaultExplicitVaultDir_DoesNotTriggerRescue_EvenWithValidLegacyDb()
    {
        // 1) Plant a VALID legacy database exactly where Program.cs looks for it.
        var legacyDir = Path.Combine(AppContext.BaseDirectory, "data");
        var legacyExistedBefore = Directory.Exists(legacyDir);
        var legacyDbPath = Path.Combine(legacyDir, "beememorybank.db");
        Directory.CreateDirectory(legacyDir);
        WriteValidSqliteMagic(legacyDbPath);

        // 2) Sandbox LOCALAPPDATA: keeps BmbPaths.DefaultVaultDir + any rescue logs hermetic AND
        //    guarantees the canonical default dir is a different physical path from our explicit
        //    non-default target.
        var fakeLocalAppData = GetTempPath("bmb-fakela");
        Directory.CreateDirectory(fakeLocalAppData);
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

        var nonDefaultTarget = GetTempPath("bmb-nondvault");
        Directory.CreateDirectory(nonDefaultTarget);

        try
        {
            // Setup invariant: the explicit target is genuinely NOT the canonical default vault
            // (otherwise this guard test would be meaningless).
            var canonicalDefault = Path.GetFullPath(BmbPaths.DefaultVaultDir);
            string.Equals(
                Path.GetFullPath(nonDefaultTarget), canonicalDefault,
                StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                    "test setup: the explicit target must NOT equal the canonical default vault");

            // Act — auto-discovery will fail (no api/web siblings under the test bin dir), but the
            // rescue guard runs strictly BEFORE auto-discovery, so exit code 1 still exercises it.
            var exitCode = await Program.RunOrchestratorAsync(
                isAutoMode: true,
                dataDirectory: nonDefaultTarget,
                configPath: null,
                stopToken: CancellationToken.None);

            exitCode.Should().Be(1);

            // Assert — CRITICAL: rescue must NOT have fired for a non-default vault.
            File.Exists(Path.Combine(nonDefaultTarget, "beememorybank.db")).Should().BeFalse(
                "rescue must not copy a legacy DB into an explicitly non-default vault");
            File.Exists(Path.Combine(nonDefaultTarget, "rescued-from.json")).Should().BeFalse(
                "no rescue marker may be written into a non-default vault");

            // No rescue log must exist either (the migration log is only written when rescue runs).
            var migrationDir = Path.Combine(fakeLocalAppData, "BeeMemoryBankData", "migration");
            var rescueLogs = Directory.Exists(migrationDir)
                ? Directory.GetFiles(migrationDir, "rescue-*.log")
                : Array.Empty<string>();
            rescueLogs.Should().BeEmpty(
                "no rescue log may exist — TryRescue must never have been called for a non-default vault");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);

            // Only remove the legacy dir WE created; never clobber a pre-existing one.
            if (!legacyExistedBefore)
            {
                try { if (Directory.Exists(legacyDir)) Directory.Delete(legacyDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    [Fact]
    public async Task RunOrchestratorAsync_ExplicitDataArg_ShouldWinOverEnvAndDefault()
    {
        // Arrange
        var explicitPath = GetTempPath("bmb-explicit");
        var envPath = GetTempPath("bmb-env");

        Environment.SetEnvironmentVariable("BMB_DATA_PATH", envPath);

        // Act
        // Auto-discovery will fail because api/web siblings don't exist under AppContext.BaseDirectory,
        // which returns 1. But the directory should still be resolved and created first.
        var exitCode = await Program.RunOrchestratorAsync(
            isAutoMode: true,
            dataDirectory: explicitPath,
            configPath: null,
            stopToken: CancellationToken.None);

        // Assert
        exitCode.Should().Be(1); // auto-discovery failed exit code
        Directory.Exists(explicitPath).Should().BeTrue();
        Directory.Exists(envPath).Should().BeFalse();
    }

    [Fact]
    public async Task RunOrchestratorAsync_EnvVar_ShouldWinOverDefault_WhenNoExplicitArg()
    {
        // Arrange
        var envPath = GetTempPath("bmb-env");
        Environment.SetEnvironmentVariable("BMB_DATA_PATH", envPath);

        // Act
        var exitCode = await Program.RunOrchestratorAsync(
            isAutoMode: true,
            dataDirectory: null,
            configPath: null,
            stopToken: CancellationToken.None);

        // Assert
        exitCode.Should().Be(1);
        Directory.Exists(envPath).Should().BeTrue();
    }

    [Fact]
    public async Task RunOrchestratorAsync_DefaultVaultDir_ShouldBeUsed_WhenNoExplicitArgAndNoEnvVar()
    {
        // Arrange
        Environment.SetEnvironmentVariable("BMB_DATA_PATH", null);

        // Sandbox BmbPaths.Root via a LOCALAPPDATA override instead of moving the real
        // default vault dir aside: on a machine with a real BeeMemoryBank install, a failed
        // Directory.Move (e.g. a sharing violation from the real app holding node.lock) used
        // to leave backupDir null while the test still ran against -- and the finally block
        // still unconditionally deleted -- the real, un-backed-up vault directory. LOCALAPPDATA
        // is honored by BmbPaths.Root (see libs/BeeMemoryBank.AppPaths/BmbPaths.cs), so this
        // is now genuinely isolated rather than relying on a move-and-restore dance.
        var fakeLocalAppData = GetTempPath("bmb-localappdata");
        Directory.CreateDirectory(fakeLocalAppData);
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

        try
        {
            var defaultVaultDir = BmbPaths.DefaultVaultDir;

            // Act
            var exitCode = await Program.RunOrchestratorAsync(
                isAutoMode: true,
                dataDirectory: null,
                configPath: null,
                stopToken: CancellationToken.None);

            // Assert
            exitCode.Should().Be(1);
            Directory.Exists(defaultVaultDir).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }
    }
}
