using BeeMemoryBank.FullIos.Services;
using Foundation;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// The node's folder in the app's container: Library/Application Support/BeeMemoryBank (the database, its journal, media). Made with the
/// file protection "complete until first user authentication" - encrypted by iOS while the phone is off or not yet unlocked since it
/// started - which every file made in it inherits, and excluded from the phone's iCloud and computer backups. The notes themselves are
/// ciphertext under the vault's master key whatever the file protection; what the protection and the backup exclusion keep in addition
/// are the titles and folder paths, which every node stores in plaintext (ADR-0005). The stronger class "complete" was not taken: SQLite
/// keeps its files open across the moment the phone locks (the last sync round when the app leaves the screen), and a file that turns
/// unreadable under an open database fails that write.
/// </summary>
internal static class IosContainer
{
    public static FullNodePaths Prepare()
    {
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        var folder = "BeeMemoryBank";
#if DEBUG || BMB_E2E
        // The end-to-end check starts every run on a vault of its own, next to the app's, instead of wiping anything.
        if (Environment.GetEnvironmentVariable("BMB_E2E_DATA") is { Length: > 0 } run && run.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            folder = "BeeMemoryBank-e2e-" + run;
#endif
        var paths = new FullNodePaths(Path.Combine(support, folder));
        Directory.CreateDirectory(paths.Media);
        Protect(paths.DataDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.DataDirectory, "*", SearchOption.AllDirectories)) Protect(entry);
        ExcludeFromBackup(paths.DataDirectory);
        return paths;
    }

    private static void Protect(string path)
    {
        var attributes = new NSFileAttributes { ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication };
        NSFileManager.DefaultManager.SetAttributes(attributes, path, out _);
    }

    private static void ExcludeFromBackup(string path)
    {
        using var url = NSUrl.FromFilename(path);
        url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
    }
}
