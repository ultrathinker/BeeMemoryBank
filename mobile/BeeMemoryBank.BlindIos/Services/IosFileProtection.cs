using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The two file attributes the iPhone copy puts on its folders, behind a seam so that the rule "EVERY account folder gets them" is tested anywhere:
/// the real calls (NSFileManager) are <c>Platforms/iOS/IosFileAttributes.cs</c>.
/// </summary>
public interface IIosFileProtection
{
    /// <summary>"Complete until first user authentication": encrypted by iOS until the first unlock after a restart, readable afterwards, also while locked, so that a background round can sync. Files made in a folder inherit it.</summary>
    void Protect(string path);

    /// <summary>Leaves the folder (and everything in it) out of the iCloud and computer backups of the phone: the copy is ciphertext whose keys never leave the phone.</summary>
    void ExcludeFromBackup(string path);
}

/// <summary>
/// Which folders get the attributes: the app's root (the 2.6 copy, account 1, lives in it), the <c>accounts</c> folder, and the data folder of EVERY account - at the root's
/// start and each time an account's runtime is built, so that a folder the host has just made (a new account, an account set up again after a wipe) never stays without them.
/// Missing paths are skipped (the host makes the folder, this never does); a path that cannot be marked is not an error of the app, as before.
/// </summary>
public static class IosAccountFolders
{
    /// <summary>The root and the files in it, at the app's start. Makes the root when it is not there.</summary>
    public static void PrepareRoot(IIosFileProtection files, string root)
    {
        Directory.CreateDirectory(root);
        files.Protect(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root)) files.Protect(entry);
        files.ExcludeFromBackup(root);
    }

    /// <summary>
    /// One account's data folder, after the host made it: the folders from the root down to it (the root, <c>accounts</c>, the account's own) and the files already in it. The legacy
    /// account's folder is the root, which <see cref="PrepareRoot"/> has done; it is done again here because a wipe may have removed and re-made files in it.
    /// </summary>
    public static void PrepareAccount(IIosFileProtection files, string root, BlindAccountEntry account)
    {
        var folder = BlindAccountNames.DataDirectory(root, account);
        var chain = new List<string> { root };
        if (!account.IsLegacy)
        {
            chain.Add(Path.Combine(root, BlindAccountRegistry.AccountsFolder));
            chain.Add(folder);
        }
        foreach (var path in chain.Where(Directory.Exists))
        {
            files.Protect(path);
            files.ExcludeFromBackup(path);
        }
        if (!Directory.Exists(folder)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) files.Protect(entry);
    }
}
