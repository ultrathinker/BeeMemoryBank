using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Platforms.iOS;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // The full node, in process: the vault's libraries over the database in the app's container (IosContainer: file protection, no
        // device backup). The app's own container holds this composition's singletons - the session (the open vault's key) among them.
        builder.Services.AddFullNode(IosContainer.Prepare());
        builder.Services.AddSingleton<FullVault>();
        builder.Services.AddSingleton<IUnlockKeyStore, KeychainUnlockKeyStore>();
        builder.Services.AddSingleton<QuickUnlock>();
        builder.Services.AddSingleton<NotesService>();
        builder.Services.AddSingleton<FullSync>();
        builder.Services.AddSingleton<AutoLockPolicy>();
        builder.Services.AddSingleton<AppFlow>();

        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<CreateVaultPage>();
        builder.Services.AddTransient<JoinPage>();
        builder.Services.AddTransient<RecoveryKeyPage>();
        builder.Services.AddTransient<UnlockPage>();
        builder.Services.AddTransient<FolderPage>();
        builder.Services.AddTransient<NotePage>();
        builder.Services.AddTransient<EditNotePage>();
        builder.Services.AddTransient<SearchPage>();
        builder.Services.AddTransient<TrashPage>();
        builder.Services.AddTransient<SyncPage>();
        builder.Services.AddTransient<SettingsPage>();
        return builder.Build();
    }
}
