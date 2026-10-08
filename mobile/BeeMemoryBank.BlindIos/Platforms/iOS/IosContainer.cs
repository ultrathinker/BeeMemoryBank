using BeeMemoryBank.BlindIos.Services;
using Foundation;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The copy's folder in the app's container: Library/Application Support/BeeMemoryBankBlind. Made with the file protection
/// "complete until first user authentication" - encrypted by iOS while the phone is off or not yet unlocked since it started, readable
/// afterwards, also while locked, so that a background round can sync - which every file made in it inherits; and excluded from the
/// iCloud and computer backups of the phone: the copy is ciphertext anyway, its keys never leave the phone, and a backup restored on
/// another phone would hold a database without its keys (the app would refuse it and ask for a wipe).
/// </summary>
internal static class IosContainer
{
    public static IosBlindPaths Prepare()
    {
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        var paths = new IosBlindPaths(support);
        Directory.CreateDirectory(paths.DataDirectory);
        Protect(paths.DataDirectory);
        foreach (var file in Directory.EnumerateFileSystemEntries(paths.DataDirectory)) Protect(file);
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
