using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BeeMemoryBank.AppPaths;

// ---------------------------------------------------------------------------
// Result types
// ---------------------------------------------------------------------------

/// <summary>
/// Outcome of a <see cref="LegacyDataRescue.TryRescue"/> call.
/// </summary>
public enum RescueOutcome
{
    /// <summary>No valid legacy database was found — normal cold start, nothing to do.</summary>
    NoLegacyFound,

    /// <summary>
    /// The target vault already contained a valid database and no conflict was detected.
    /// No-op, data is safe.
    /// </summary>
    TargetAlreadyValid,

    /// <summary>Legacy data was successfully copied into the default target vault.</summary>
    RescuedSuccessfully,

    /// <summary>
    /// Both the legacy and target vaults contained valid but different databases.
    /// Legacy was copied into a new <c>recovered-&lt;date&gt;</c> vault without touching the
    /// existing target vault.
    /// </summary>
    RescuedToRecoveredVault,

    /// <summary>
    /// A valid legacy database was detected but the copy operation failed.
    /// The caller MUST NOT start the node with an empty vault — surface the error.
    /// </summary>
    LegacyFoundButRescueFailed,
}

/// <summary>
/// Result returned by <see cref="LegacyDataRescue.TryRescue"/>.
/// </summary>
public sealed record RescueResult(
    RescueOutcome Outcome,
    /// <summary>
    /// The vault directory that received (or already contained) the data.
    /// <c>null</c> when <see cref="RescueOutcome.NoLegacyFound"/> or
    /// <see cref="RescueOutcome.LegacyFoundButRescueFailed"/>.
    /// </summary>
    string? VaultDir,
    /// <summary>Human-readable reason for failure or additional context.</summary>
    string? Message
)
{
    public static RescueResult NoLegacy() =>
        new(RescueOutcome.NoLegacyFound, null, null);

    public static RescueResult AlreadyValid(string vaultDir) =>
        new(RescueOutcome.TargetAlreadyValid, vaultDir, null);

    public static RescueResult Rescued(string vaultDir, string message) =>
        new(RescueOutcome.RescuedSuccessfully, vaultDir, message);

    public static RescueResult RescuedToRecovered(string recoveredVaultDir, string message) =>
        new(RescueOutcome.RescuedToRecoveredVault, recoveredVaultDir, message);

    public static RescueResult Failed(string reason) =>
        new(RescueOutcome.LegacyFoundButRescueFailed, null, reason);
}

// ---------------------------------------------------------------------------
// Rescue engine
// ---------------------------------------------------------------------------

/// <summary>
/// One-time, idempotent, copy-not-move rescue migration of legacy data locked inside
/// a Velopack <c>current\data\</c> directory into the new stable vault directory.
/// </summary>
/// <remarks>
/// <para>
/// The source is NEVER deleted or moved — the caller should let Velopack's own
/// uninstall/update lifecycle clean it up.
/// </para>
/// <para>
/// Safe to call multiple times; subsequent calls after a successful rescue return
/// <see cref="RescueOutcome.TargetAlreadyValid"/> immediately.
/// </para>
/// </remarks>
public static class LegacyDataRescue
{
    // SQLite magic bytes: "SQLite format 3\0" (16 bytes)
    private static readonly byte[] SqliteMagic =
        Encoding.ASCII.GetBytes("SQLite format 3\0");

    // Files that are transient/runtime-only and must NOT be copied across.
    private static readonly HashSet<string> TransientFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.lock",
        ".runtime.json",
        "node.status.json",
    };

    // Suffix pattern for *.ready transient files (checked separately, not by exact name).
    private const string ReadySuffix = ".ready";

    // Minimum byte threshold for a "real" SQLite file (checked after magic bytes).
    // Per spec: SQLite header is sufficient — size > 4096 is optional extra signal.
    // We do validate both: header magic must be present, and that implicitly means at
    // least 16 bytes, which is enough. The spec says "> 4096 bytes as minimum signal,
    // but not hard" — we don't block on size alone, only require valid header.

    // Number of bytes to hash for the "are these the same DB?" check (spec: first 64KB).
    private const int FingerprintBytes = 64 * 1024;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attempts to rescue legacy data from <paramref name="legacyDir"/> into
    /// <paramref name="targetVaultDir"/>.
    /// </summary>
    /// <param name="legacyDir">
    /// Old data directory, typically <c>&lt;AppContext.BaseDirectory&gt;\data</c>.
    /// </param>
    /// <param name="targetVaultDir">
    /// New stable vault directory — typically <see cref="BmbPaths.DefaultVaultDir"/>.
    /// </param>
    /// <returns>A <see cref="RescueResult"/> describing what happened.</returns>
    public static RescueResult TryRescue(string legacyDir, string targetVaultDir)
    {
        // ----------------------------------------------------------------
        // Step 1 — Validate legacy source
        // ----------------------------------------------------------------
        var legacyDbPath = Path.Combine(legacyDir, "beememorybank.db");

        bool legacyValid = IsSqliteFileValid(legacyDbPath);

        if (!legacyValid)
        {
            // No valid legacy source — check whether target is valid so we can
            // distinguish NoLegacyFound from TargetAlreadyValid.
            var targetDbPath2 = Path.Combine(targetVaultDir, "beememorybank.db");
            if (IsSqliteFileValid(targetDbPath2))
            {
                return RescueResult.AlreadyValid(targetVaultDir);
            }
            return RescueResult.NoLegacy();
        }

        // Check node.lock — if locked, the node is running and we must not copy.
        var legacyLockPath = Path.Combine(legacyDir, "node.lock");
        if (IsFileLocked(legacyLockPath))
        {
            return RescueResult.Failed(
                $"Legacy source is locked (node.lock is held by another process): '{legacyLockPath}'. " +
                "Stop the running node before migrating.");
        }

        // ----------------------------------------------------------------
        // Step 2/3/4 — Determine scenario
        // ----------------------------------------------------------------
        var targetDbPath = Path.Combine(targetVaultDir, "beememorybank.db");
        bool targetValid = IsSqliteFileValid(targetDbPath);

        if (!targetValid)
        {
            // Case 1: target empty/invalid, legacy valid — copy to target.
            return ExecuteRescue(legacyDir, targetVaultDir, isRecovery: false);
        }

        // Both valid — are they the same database?
        bool sameDb = AreSameDatabase(legacyDbPath, targetDbPath);
        if (sameDb)
        {
            // Case 2: target already valid and same as legacy — no-op.
            return RescueResult.AlreadyValid(targetVaultDir);
        }

        // Case 3: both valid, different databases — copy legacy to recovered vault.
        var recoveredVaultId = $"recovered-{DateTime.Now:yyyyMMdd-HHmmss}";
        // BmbPaths.VaultDir will create the directory.
        var recoveredVaultDir = BmbPaths.VaultDir(recoveredVaultId);
        return ExecuteRescue(legacyDir, recoveredVaultDir, isRecovery: true);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Performs the actual copy: legacy → temp sibling → atomic rename into target.
    /// </summary>
    private static RescueResult ExecuteRescue(string legacyDir, string targetVaultDir, bool isRecovery)
    {
        // Use a temp sibling directory so that a mid-copy failure leaves the target
        // directory absent rather than partially populated.
        var tempDir = targetVaultDir + $".rescue-tmp-{Guid.NewGuid():N}";

        int fileCount = 0;
        long totalBytes = 0;

        try
        {
            Directory.CreateDirectory(tempDir);

            // Enumerate and copy all files recursively, excluding transient ones.
            CopyDirectoryRecursive(legacyDir, tempDir, ref fileCount, ref totalBytes);

            // Handle desktop-settings.json specially — it goes to BmbPaths.Root, not vault.
            var desktopSettingsInTemp = Path.Combine(tempDir, "desktop-settings.json");
            if (File.Exists(desktopSettingsInTemp))
            {
                var desktopSettingsDest = BmbPaths.DesktopSettingsFile;
                // Only copy if the destination doesn't already exist (conservative).
                if (!File.Exists(desktopSettingsDest))
                {
                    File.Copy(desktopSettingsInTemp, desktopSettingsDest, overwrite: false);
                }
                File.Delete(desktopSettingsInTemp);
            }

            // Write rescued-from.json marker inside the temp dir before rename.
            var appVersion = GetAppVersion();
            var marker = new RescuedFromMarker(
                SourcePath: legacyDir,
                RescuedAt: DateTime.UtcNow.ToString("O"),
                AppVersion: appVersion,
                FileCount: fileCount,
                TotalBytes: totalBytes);

            var markerJson = JsonSerializer.Serialize(marker, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(Path.Combine(tempDir, "rescued-from.json"), markerJson);

            // Ensure the parent directory of targetVaultDir exists.
            var parentDir = Path.GetDirectoryName(targetVaultDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            // Atomic rename: tempDir → targetVaultDir.
            // If targetVaultDir already exists (e.g. was created empty by BmbPaths.VaultDir),
            // remove it first — it is guaranteed to be empty at this point.
            if (Directory.Exists(targetVaultDir))
            {
                // Only safe to delete if empty (guard against partial prior state).
                var entries = Directory.EnumerateFileSystemEntries(targetVaultDir);
                if (!entries.Any())
                {
                    Directory.Delete(targetVaultDir, recursive: false);
                }
                else
                {
                    // Non-empty target appeared unexpectedly — abort to be safe.
                    CleanupTemp(tempDir);
                    return RescueResult.Failed(
                        $"Target directory '{targetVaultDir}' was non-empty at the point of atomic rename. " +
                        "Rescue aborted to avoid data loss.");
                }
            }

            Directory.Move(tempDir, targetVaultDir);

            // Write migration log.
            WriteMigrationLog(legacyDir, targetVaultDir, fileCount, totalBytes, isRecovery, null);

            var msg = isRecovery
                ? $"Legacy data rescued to recovered vault '{targetVaultDir}' ({fileCount} files, {totalBytes} bytes)."
                : $"Legacy data rescued to default vault '{targetVaultDir}' ({fileCount} files, {totalBytes} bytes).";

            return isRecovery
                ? RescueResult.RescuedToRecovered(targetVaultDir, msg)
                : RescueResult.Rescued(targetVaultDir, msg);
        }
        catch (Exception ex)
        {
            CleanupTemp(tempDir);
            WriteMigrationLog(legacyDir, targetVaultDir, fileCount, totalBytes, isRecovery, ex.Message);
            return RescueResult.Failed(
                $"Rescue copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively copies <paramref name="sourceDir"/> into <paramref name="destDir"/>,
    /// skipping transient files.
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir, ref int fileCount, ref long totalBytes)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            if (IsTransient(fileName))
            {
                continue;
            }

            var destFile = Path.Combine(destDir, fileName);
            File.Copy(file, destFile, overwrite: true);
            fileCount++;
            totalBytes += new FileInfo(file).Length;
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            var destSubDir = Path.Combine(destDir, dirName);
            CopyDirectoryRecursive(subDir, destSubDir, ref fileCount, ref totalBytes);
        }
    }

    /// <summary>Returns true if the file name represents a transient runtime file.</summary>
    private static bool IsTransient(string fileName)
    {
        if (TransientFileNames.Contains(fileName))
        {
            return true;
        }

        if (fileName.EndsWith(ReadySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the file at <paramref name="path"/> exists and starts with the
    /// SQLite magic header bytes.
    /// </summary>
    private static bool IsSqliteFileValid(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < SqliteMagic.Length)
            {
                return false;
            }

            var header = new byte[SqliteMagic.Length];
            int read = fs.Read(header, 0, header.Length);
            if (read < SqliteMagic.Length)
            {
                return false;
            }

            for (int i = 0; i < SqliteMagic.Length; i++)
            {
                if (header[i] != SqliteMagic[i])
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to open <paramref name="lockPath"/> with exclusive write access.
    /// Returns true if the file is locked by another process.
    /// </summary>
    private static bool IsFileLocked(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            // Opened exclusively — not locked.
            return false;
        }
        catch (IOException)
        {
            // Could not open exclusively — file is locked.
            return true;
        }
        catch
        {
            // Any other error (e.g. permissions) — treat as not locked to avoid blocking rescue
            // on an inaccessible but uncontended file.
            return false;
        }
    }

    /// <summary>
    /// Conservative "same database" check. Returns false (different) if ANY of the
    /// following differ: file size, last-write time, or SHA-256 of the first 64 KB.
    /// Per spec: when in doubt, treat as different.
    /// </summary>
    private static bool AreSameDatabase(string pathA, string pathB)
    {
        try
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);

            if (infoA.Length != infoB.Length)
            {
                return false;
            }

            if (infoA.LastWriteTimeUtc != infoB.LastWriteTimeUtc)
            {
                return false;
            }

            // Hash first FingerprintBytes bytes of each file.
            var hashA = HashFilePrefix(pathA);
            var hashB = HashFilePrefix(pathB);

            if (hashA == null || hashB == null)
            {
                // Could not read — conservatively treat as different.
                return false;
            }

            return hashA.SequenceEqual(hashB);
        }
        catch
        {
            // On any error, conservatively report different.
            return false;
        }
    }

    private static byte[]? HashFilePrefix(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytesToRead = (int)Math.Min(fs.Length, FingerprintBytes);
            var buffer = new byte[bytesToRead];
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = fs.Read(buffer, totalRead, bytesToRead - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            return SHA256.HashData(buffer.AsSpan(0, totalRead));
        }
        catch
        {
            return null;
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            return System.Reflection.Assembly
                .GetEntryAssembly()?
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is System.Reflection.AssemblyInformationalVersionAttribute[] attrs && attrs.Length > 0
                    ? attrs[0].InformationalVersion
                    : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void WriteMigrationLog(
        string legacyDir,
        string targetDir,
        int fileCount,
        long totalBytes,
        bool isRecovery,
        string? error)
    {
        try
        {
            var migrationDir = BmbPaths.MigrationDir;
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            var logFile = Path.Combine(migrationDir, $"rescue-{timestamp}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] Legacy Data Rescue");
            sb.AppendLine($"  Source  : {legacyDir}");
            sb.AppendLine($"  Target  : {targetDir}");
            sb.AppendLine($"  Recovery: {isRecovery}");
            sb.AppendLine($"  Files   : {fileCount}");
            sb.AppendLine($"  Bytes   : {totalBytes}");
            if (error != null)
            {
                sb.AppendLine($"  Error   : {error}");
            }

            File.WriteAllText(logFile, sb.ToString());
        }
        catch
        {
            // Logging failure must not abort or change the rescue result.
        }
    }

    private static void CleanupTemp(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort — leave the temp dir if we cannot clean it.
        }
    }

    // -----------------------------------------------------------------------
    // Marker record (for rescued-from.json)
    // -----------------------------------------------------------------------

    private sealed record RescuedFromMarker(
        string SourcePath,
        string RescuedAt,
        string AppVersion,
        int FileCount,
        long TotalBytes);
}
