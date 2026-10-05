using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// Where the Windows blind app keeps everything: <c>%LOCALAPPDATA%\BeeMemoryBankBlind</c>. A root of its own, never the
/// full app's (that one lives under ProgramData or its own profile folders), so the two apps can sit on one PC without
/// ever sharing a database, a key or a log.
/// </summary>
public sealed class WindowsBlindPaths : IBlindPaths
{
    public const string FolderName = "BeeMemoryBankBlind";
    public const string DatabaseFileName = "beememorybank.db";

    public WindowsBlindPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? DefaultRoot());
    }

    public string Root { get; }

    /// <summary>The app-private folder; <c>BlindPhoneReset</c> removes the database, replica, media, backups and log from it.</summary>
    public string DataDirectory => Root;

    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    /// <summary>DPAPI-protected blobs: identity seed, backup key, pairing secret. Never inside the SQLite database.</summary>
    public string SecretsDirectory => Path.Combine(Root, "secrets");

    /// <summary>The small key/value state (call code, load/sync/backup times, schedule); nothing secret.</summary>
    public string StatePath => Path.Combine(Root, "state.json");

    public static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("Windows did not give this user a LocalApplicationData folder.");
        return Path.Combine(local, FolderName);
    }
}
