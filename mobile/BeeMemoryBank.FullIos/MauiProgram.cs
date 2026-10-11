using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Platforms.iOS;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.FullIos;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // The full node, in process: the vault's libraries over the database in the app's container (IosContainer: file protection, no
        // device backup). The app's own container holds this composition's singletons - the session (the open vault's key) among them.
        var container = IosContainer.Prepare();
        builder.Services.AddFullNode(container);

        // Step B (docs/design/step-b-spec.md 5.8, 12.2): a phone that may be encrypted, is encrypted, or holds an interrupted encryption starts through
        // the vault lifecycle - the interrupted swap is finished or undone, the folder diagnosed, the lease held. While encryption is hidden and the
        // folder is plain this is skipped and the app starts exactly as it did before.
        if (PhoneVaultLifecycle.NeedsGate(container.DataDirectory))
        {
            var phoneStart = PhoneVaultLifecycle.Begin(container.DataDirectory);
            builder.Services.AddSingleton<IPhoneEnvironment, IosPhoneEnvironment>();
            builder.Services.AddPhoneVault(container.DataDirectory, phoneStart, PhoneVaultLifecycle.PhoneEncryptionAvailable);
            builder.Services.AddOpenWork();
        }
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
        builder.Services.AddTransient<EncryptDatabasePage>();
        return builder.Build();
    }
}
