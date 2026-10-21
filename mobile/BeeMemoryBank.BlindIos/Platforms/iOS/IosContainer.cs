using BeeMemoryBank.BlindIos.Services;
using Foundation;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The app's folder in its container: Library/Application Support/BeeMemoryBankBlind. It holds the 2.6 copy (account 1) as it always did, the list of accounts, and
/// <c>accounts/&lt;id&gt;/</c> for every other account. The root is made with the file protection "complete until first user authentication" and left out of the iCloud and
/// computer backups (see <see cref="IosFileAttributes"/>), and so is every account's folder when its runtime is built (<see cref="IosAccountFolders"/>).
/// </summary>
internal static class IosContainer
{
    public static IosBlindPaths Prepare(IIosFileProtection files)
    {
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        var paths = new IosBlindPaths(support);
        IosAccountFolders.PrepareRoot(files, paths.DataDirectory);
        return paths;
    }
}
