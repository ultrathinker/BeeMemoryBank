using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// The one wipe of the app's vault files, for every wipe the app has: "delete all data" in the settings, "cancel and delete" of the first sync and
/// the setup reset. It deletes the whole sealed file set - the boot store, the database, the chat database, the password-hint
/// mirror and the seal journal, each with its sidecars - and returns the gate to plain, as the node's reset does. Deleting the database alone left
/// the boot store behind, and the next start of an encrypted phone was a dead end with nothing to tap. Nothing else in the app's folder is touched;
/// a file that cannot be deleted throws, so a wipe never says done over data that is still there.
/// </summary>
public static class PhoneWipe
{
    /// <summary>The app's data folder: the vault's files live here.</summary>
    public static string DataDirectory => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static async Task ReturnToPlainAsync(IServiceProvider services)
    {
        if (services.GetService<PhoneVaultLifecycle>() is { } lifecycle)
        {
            await lifecycle.ReturnToPlainAsync(DataDirectory);
            return;
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        PhoneVaultLifecycle.DeleteVaultFiles(DataDirectory);
    }
}
