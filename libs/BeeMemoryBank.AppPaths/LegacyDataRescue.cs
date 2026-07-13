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
// Tri-state for SQLite file validation (Fix #1)
// ---------------------------------------------------------------------------

/// <summary>
/// Three-way result of probing a SQLite file path, distinguishing the "file
/// exists but is currently unreadable" case from "file absent".
/// </summary>
internal enum SqliteFileStatus
{
    /// <summary>No file found at the given path.</summary>
    FileNotFound,

    /// <summary>File exists but cannot be opened or read (ACL, AV lock, sharing violation…).</summary>
    Unreadable,

    /// <summary>File exists but does not start with the SQLite magic header.</summary>
    InvalidHeader,

    /// <summary>File exists and has a valid SQLite magic header.</summary>
    ValidSqlite,
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

        // Fix #1: tri-state probe — distinguish "file absent" from "file unreadable".
        var legacyStatus = ProbeSqliteFile(legacyDbPath);

        if (legacyStatus == SqliteFileStatus.Unreadable)
        {
            // File EXISTS but we cannot read it (ACL/AV/sharing). Returning
            // NoLegacyFound here would silently start with empty storage — refuse instead.
            return RescueResult.Failed(
                $"Legacy database exists but is currently unreadable: '{legacyDbPath}'. " +
                "Check for running processes or security software holding the file, then retry.");
        }

        if (legacyStatus != SqliteFileStatus.ValidSqlite)
        {
            // FileNotFound or InvalidHeader — no valid legacy, check target.
            var targetDbPath2 = Path.Combine(targetVaultDir, "beememorybank.db");
            var targetStatus2 = ProbeSqliteFile(targetDbPath2);
            if (targetStatus2 == SqliteFileStatus.ValidSqlite)
            {
                return RescueResult.AlreadyValid(targetVaultDir);
            }
            return RescueResult.NoLegacy();
        }

        // Fix #2: Acquire an exclusive hold on node.lock and keep it open throughout the copy,
        // preventing the legacy node from starting between the check and the copy completing.
        var legacyLockPath = Path.Combine(legacyDir, "node.lock");
        FileStream? lockHold = null;

        if (File.Exists(legacyLockPath))
        {
            try
            {
                // Open with FileShare.None — if the node is running this will throw IOException.
                lockHold = new FileStream(legacyLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return RescueResult.Failed(
                    $"Legacy source is locked (node.lock is held by another process): '{legacyLockPath}'. " +
                    "Stop the running node before migrating.");
            }
            catch (Exception ex)
            {
                return RescueResult.Failed(
                    $"Cannot acquire exclusive hold on '{legacyLockPath}': {ex.Message}");
            }
        }

        try
        {
            // ----------------------------------------------------------------
            // Step 2/3/4 — Determine scenario
            // ----------------------------------------------------------------
            var targetDbPath = Path.Combine(targetVaultDir, "beememorybank.db");
            var targetStatus = ProbeSqliteFile(targetDbPath);

            if (targetStatus != SqliteFileStatus.ValidSqlite)
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
        finally
        {
            // Fix #2: release the lock hold after the copy is fully done (or failed).
            lockHold?.Dispose();
        }
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
                    // Fix #5: Non-empty target appeared unexpectedly — could be a concurrent rescue
                    // that already succeeded. Re-validate before treating as failure.
                    var targetDbPath = Path.Combine(targetVaultDir, "beememorybank.db");
                    var recheck = ProbeSqliteFile(targetDbPath);
                    if (recheck == SqliteFileStatus.ValidSqlite)
                    {
                        // Another concurrent rescue already populated the target successfully.
                        CleanupTemp(tempDir);
                        return RescueResult.AlreadyValid(targetVaultDir);
                    }

                    // Target is non-empty but does NOT contain a valid DB — it's a genuine
                    // unexpected state (partial state from a previous failed attempt, etc.).
                    // Try recovering to a dated vault rather than failing outright, but only
                    // when we are not already in a recovery attempt (avoid infinite recursion).
                    CleanupTemp(tempDir);
                    if (!isRecovery)
                    {
                        var recoveredVaultId = $"recovered-{DateTime.Now:yyyyMMdd-HHmmss}";
                        var recoveredVaultDir = BmbPaths.VaultDir(recoveredVaultId);
                        return ExecuteRescue(legacyDir, recoveredVaultDir, isRecovery: true);
                    }

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
    /// skipping transient files and reparse points (junctions/symlinks).
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
            // Fix #3: Skip reparse points (junctions/symlinks) to prevent infinite
            // recursion and runaway disk usage from looped or huge external trees.
            var dirInfo = new DirectoryInfo(subDir);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Log to console (best effort); the rescue must not crash on this.
                try
                {
                    Console.WriteLine($"[LegacyDataRescue] Skipping reparse point/junction: '{subDir}'");
                }
                catch { }
                continue;
            }

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
    /// Fix #1: Three-way probe of a SQLite file path.
    /// Distinguishes "file absent" from "file unreadable" from "file valid/invalid".
    /// </summary>
    internal static SqliteFileStatus ProbeSqliteFile(string path)
    {
        if (!File.Exists(path))
        {
            return SqliteFileStatus.FileNotFound;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < SqliteMagic.Length)
            {
                return SqliteFileStatus.InvalidHeader;
            }

            var header = new byte[SqliteMagic.Length];
            int read = fs.Read(header, 0, header.Length);
            if (read < SqliteMagic.Length)
            {
                return SqliteFileStatus.InvalidHeader;
            }

            for (int i = 0; i < SqliteMagic.Length; i++)
            {
                if (header[i] != SqliteMagic[i])
                {
                    return SqliteFileStatus.InvalidHeader;
                }
            }

            return SqliteFileStatus.ValidSqlite;
        }
        catch (IOException)
        {
            // File exists but cannot be opened — AV lock, sharing violation, permissions, etc.
            return SqliteFileStatus.Unreadable;
        }
        catch (UnauthorizedAccessException)
        {
            return SqliteFileStatus.Unreadable;
        }
        catch
        {
            // Any other unexpected error — treat as unreadable to be safe.
            return SqliteFileStatus.Unreadable;
        }
    }

    /// <summary>
    /// Fix #6: Conservative "same database" check. Returns false (different) if ANY of the
    /// following differ: file size, last-write time, or full-file SHA-256.
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

            // Fix #6: Hash the ENTIRE file (streaming, not buffered into memory at once)
            // to avoid false-positive "same" decisions on files that differ only beyond 64 KB.
            var hashA = HashFullFile(pathA);
            var hashB = HashFullFile(pathB);

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

    /// <summary>
    /// Fix #6: Computes SHA-256 over the entire file content by streaming it, so that large
    /// files are never loaded fully into memory.
    /// </summary>
    private static byte[]? HashFullFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 81920, useAsync: false);
            using var sha = SHA256.Create();
            return sha.ComputeHash(fs);
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
                .GetEntryAssembly()
                ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is System.Reflection.AssemblyInformationalVersionAttribute[] attrs && attrs.Length > 0
                    ? attrs[0].InformationalVersion
                    : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Fix #7: Writes the migration log to BOTH <see cref="BmbPaths.MigrationDir"/> and
    /// <see cref="BmbPaths.LogsDir"/> as required by the spec (§3.2 point 5).
    /// </summary>
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
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
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

            var content = sb.ToString();

            // Primary location: migration\
            var migrationDir = BmbPaths.MigrationDir;
            File.WriteAllText(Path.Combine(migrationDir, $"rescue-{timestamp}.log"), content);

            // Secondary location: logs\ (spec §3.2 point 5)
            var logsDir = BmbPaths.LogsDir;
            File.WriteAllText(Path.Combine(logsDir, $"rescue-{timestamp}.log"), content);
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
