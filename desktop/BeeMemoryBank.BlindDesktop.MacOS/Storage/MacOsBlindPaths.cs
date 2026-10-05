using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>
/// Where the macOS blind app keeps its data: <c>~/Library/Application Support/BeeMemoryBankBlind</c>, created with mode 0700 (only the
/// user). The folder holds the replica database (<c>beememorybank.db</c>, the name Blind.AppCore's wipe removes), the media, backups and log
/// the core defines, and the host's state file. It is deliberately a folder of its own and never anywhere under the full app's
/// <c>BeeMemoryBank*</c> data folders.
/// </summary>
public sealed class MacOsBlindPaths : IBlindPaths
{
    public const string FolderName = "BeeMemoryBankBlind";
    public const string DatabaseFileName = "beememorybank.db";

    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public string DataDirectory { get; }
    public string DatabasePath { get; }

    /// <param name="applicationSupportRoot">
    /// The folder that contains the app's folder; null means the user's <c>~/Library/Application Support</c>. Tests pass a temporary folder.
    /// </param>
    /// <param name="createDirectory">Create the folder (mode 0700) now. The default; false only for tests of the path arithmetic.</param>
    public MacOsBlindPaths(string? applicationSupportRoot = null, bool createDirectory = true)
    {
        var root = applicationSupportRoot ?? DefaultApplicationSupport();
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("An Application Support folder is needed.", nameof(applicationSupportRoot));
        DataDirectory = Path.Combine(Path.GetFullPath(root), FolderName);
        DatabasePath = Path.Combine(DataDirectory, DatabaseFileName);
        if (createDirectory) EnsureCreated();
    }

    /// <summary>
    /// The data folder at exactly this place (not a <c>BeeMemoryBankBlind</c> folder inside it): for a scratch or test folder chosen with
    /// <c>--data-dir</c>. Created with mode 0700 like the default one.
    /// </summary>
    public static MacOsBlindPaths AtDirectory(string dataDirectory, bool createDirectory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);   // before GetFullPath: on Unix a blank name is a valid file name
        return new MacOsBlindPaths(Path.GetFullPath(dataDirectory), createDirectory, exact: true);
    }

    private MacOsBlindPaths(string dataDirectory, bool createDirectory, bool exact)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("A data folder is needed.", nameof(dataDirectory));
        DataDirectory = dataDirectory;
        DatabasePath = Path.Combine(DataDirectory, DatabaseFileName);
        if (createDirectory) EnsureCreated();
    }

    /// <summary>The user's <c>~/Library/Application Support</c>.</summary>
    public static string DefaultApplicationSupport()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("The user's home folder cannot be determined.");
        return Path.Combine(home, "Library", "Application Support");
    }

    /// <summary>Creates the data folder with mode 0700, or tightens an existing one that is wider. Safe to call again.</summary>
    public void EnsureCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            // Only so the pure logic can be exercised on a development PC; the app is a macOS app.
            Directory.CreateDirectory(DataDirectory);
            return;
        }
        var parent = Path.GetDirectoryName(DataDirectory)!;
        Directory.CreateDirectory(parent);
        if (!Directory.Exists(DataDirectory)) Directory.CreateDirectory(DataDirectory, PrivateDirectory);
        if (File.GetUnixFileMode(DataDirectory) != PrivateDirectory) File.SetUnixFileMode(DataDirectory, PrivateDirectory);
    }
}
