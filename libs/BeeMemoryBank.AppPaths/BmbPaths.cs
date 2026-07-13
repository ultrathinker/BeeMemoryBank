using System;
using System.IO;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// Provides path resolution and invariants for BeeMemoryBank application data directory paths.
/// </summary>
public static class BmbPaths
{
    private static readonly char[] InvalidVaultIdChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// The default vault identifier constant.
    /// </summary>
    public const string DefaultVaultId = "default";

    /// <summary>
    /// Gets the root data directory path: %LOCALAPPDATA%\BeeMemoryBankData.
    /// Guarantees that the directory exists.
    /// </summary>
    public static string Root
    {
        get
        {
            // Future branch for macOS/Linux:
            // On macOS: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", "BeeMemoryBankData")
            // On Linux: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "BeeMemoryBankData")

            // Prefer the LOCALAPPDATA environment variable over
            // Environment.GetFolderPath(SpecialFolder.LocalApplicationData): on Windows,
            // GetFolderPath resolves via the Known Folder API and does NOT observe
            // Environment.SetEnvironmentVariable("LOCALAPPDATA", ...) overrides, which makes
            // it impossible to sandbox in-process tests. In a normal user session the two
            // agree, so this changes nothing for real users.
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } envValue
                ? envValue
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(localAppData, "BeeMemoryBankData");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Gets the profiles registry file path: &lt;Root&gt;\profiles.json.
    /// Guarantees that the parent directory exists.
    /// </summary>
    public static string ProfilesFile
    {
        get
        {
            string root = Root;
            return Path.Combine(root, "profiles.json");
        }
    }

    /// <summary>
    /// Gets the desktop settings file path: &lt;Root&gt;\desktop-settings.json.
    /// Guarantees that the parent directory exists.
    /// </summary>
    public static string DesktopSettingsFile
    {
        get
        {
            string root = Root;
            return Path.Combine(root, "desktop-settings.json");
        }
    }

    /// <summary>
    /// Gets the logs directory path: &lt;Root&gt;\logs.
    /// Guarantees that the directory exists.
    /// </summary>
    public static string LogsDir
    {
        get
        {
            string path = Path.Combine(Root, "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Gets the migration directory path: &lt;Root&gt;\migration.
    /// Guarantees that the directory exists.
    /// </summary>
    public static string MigrationDir
    {
        get
        {
            string path = Path.Combine(Root, "migration");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Gets the vaults parent directory path: &lt;Root&gt;\vaults.
    /// Guarantees that the directory exists.
    /// </summary>
    public static string VaultsDir
    {
        get
        {
            string path = Path.Combine(Root, "vaults");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Gets the default vault directory path: &lt;VaultsDir&gt;\default.
    /// Guarantees that the directory exists.
    /// </summary>
    public static string DefaultVaultDir => VaultDir(DefaultVaultId);

    /// <summary>
    /// Gets the vault directory path for a specific vault ID.
    /// Guarantees that the directory exists.
    /// Validates the vault ID to prevent path traversal attacks.
    /// </summary>
    /// <param name="vaultId">The vault identifier.</param>
    /// <returns>The path to the vault directory.</returns>
    /// <exception cref="ArgumentException">Thrown when the vault ID is invalid or attempts path traversal.</exception>
    public static string VaultDir(string vaultId)
    {
        if (string.IsNullOrWhiteSpace(vaultId))
        {
            throw new ArgumentException("Vault ID cannot be null, empty, or whitespace.", nameof(vaultId));
        }

        if (vaultId == "." || vaultId == "..")
        {
            throw new ArgumentException("Vault ID cannot be '.' or '..'.", nameof(vaultId));
        }

        if (vaultId.Contains(".."))
        {
            throw new ArgumentException("Vault ID cannot contain '..' path traversal sequence.", nameof(vaultId));
        }

        if (vaultId.IndexOfAny(InvalidVaultIdChars) >= 0)
        {
            throw new ArgumentException("Vault ID contains invalid characters. Forbidden: / \\ : * ? \" < > |", nameof(vaultId));
        }

        string vaultsDir = VaultsDir;
        string vaultDir = Path.Combine(vaultsDir, vaultId);
        
        // Final sanity check using absolute paths to prevent any path traversal escaping VaultsDir
        string fullVaultsDir = Path.GetFullPath(vaultsDir);
        string fullVaultDir = Path.GetFullPath(vaultDir);

        var comparison = System.OperatingSystem.IsWindows() 
            ? StringComparison.OrdinalIgnoreCase 
            : StringComparison.Ordinal;

        string? parentDir = Path.GetDirectoryName(fullVaultDir);
        if (parentDir == null || !string.Equals(parentDir, fullVaultsDir, comparison))
        {
            throw new ArgumentException("Vault ID is invalid and attempted path traversal.", nameof(vaultId));
        }

        Directory.CreateDirectory(fullVaultDir);
        return fullVaultDir;
    }

    /// <summary>
    /// True if <paramref name="path"/> has an ancestor directory literally named "current" that
    /// has a sibling Update.exe — the exact, deployment-agnostic signature of a Velopack
    /// live-payload folder. Such a folder is entirely wiped and replaced on every apply, so any
    /// data living inside it would be destroyed on the next update.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="ProfileService.AddProfile"/> (or equivalent) callers that accept a
    /// user-supplied explicit vault path, and by <c>UpdateService</c>'s own pre-apply guard —
    /// both need the SAME answer to "would this path get destroyed by an update", so this lives
    /// in one place instead of two copies silently drifting apart.
    /// </remarks>
    public static bool IsInsideVelopackCurrentDir(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir != null)
            {
                if (string.Equals(dir.Name, "current", StringComparison.OrdinalIgnoreCase)
                    && dir.Parent != null
                    && File.Exists(Path.Combine(dir.Parent.FullName, "Update.exe")))
                {
                    return true;
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // Any path-resolution error is not our concern here — callers' own checks
            // (SnapshotService, DB validation, etc.) will surface real problems.
        }
        return false;
    }
}
