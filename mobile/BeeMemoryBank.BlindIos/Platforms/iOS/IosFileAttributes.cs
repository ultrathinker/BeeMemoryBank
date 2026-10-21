using BeeMemoryBank.BlindIos.Services;
using Foundation;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The file attributes the copy's folders get, through NSFileManager. "Complete until first user authentication": encrypted by iOS while the phone is off or not yet unlocked since it
/// started, readable afterwards, also while locked, so that a background round can sync - which every file made in the folder inherits. Excluded from the iCloud and computer backups of
/// the phone: the copy is ciphertext anyway, its keys never leave the phone, and a backup restored on another phone would hold a database without its keys (the app would refuse it and ask
/// for a wipe). Which folders get them is <see cref="IosAccountFolders"/>, tested on any OS.
/// </summary>
public sealed class IosFileAttributes : IIosFileProtection
{
    public void Protect(string path)
    {
        var attributes = new NSFileAttributes { ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication };
        NSFileManager.DefaultManager.SetAttributes(attributes, path, out _);
    }

    public void ExcludeFromBackup(string path)
    {
        using var url = NSUrl.FromFilename(path);
        url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
    }
}
