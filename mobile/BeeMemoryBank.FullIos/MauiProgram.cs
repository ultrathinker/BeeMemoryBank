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

        // The design's input shells on the native fields (Controls/IosFieldLook).
        Controls.IosFieldLook.Apply();

        // The design's faces (Resources/Fonts, linked from the Android app, SIL OFL). Plus Jakarta
        // Sans carries no Cyrillic, so it is only used for the Latin brand name; every localized
        // title uses Noto Sans, exactly what the design's font stack falls back to.
        builder.ConfigureFonts(fonts =>
        {
            fonts.AddFont("NotoSans-Regular.ttf", "NotoSans");
            fonts.AddFont("NotoSans-Medium.ttf", "NotoSansMedium");
            fonts.AddFont("NotoSans-SemiBold.ttf", "NotoSansSemibold");
            fonts.AddFont("NotoSans-Bold.ttf", "NotoSansBold");
            fonts.AddFont("PlusJakartaSans-SemiBold.ttf", "JakartaSemibold");
            fonts.AddFont("PlusJakartaSans-Bold.ttf", "JakartaBold");
        });

        // The full node, in process: the vault's libraries over the database in the app's container (IosContainer: file protection, no
        // device backup). The app's own container holds this composition's singletons - the session (the open vault's key) among them.
        var container = IosContainer.Prepare();
        builder.Services.AddSingleton(container);
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
        builder.Services.AddSingleton<SecurityOps>();
        builder.Services.AddSingleton<PhoneSealing>();
        builder.Services.AddSingleton<FullSync>();
        // The phone's discovery of its computers on the Wi-Fi: Network.framework's browse under the platform-free logic (Services/BonjourPeerDiscovery).
        builder.Services.AddSingleton<IBonjourBrowser, NwBonjourBrowser>();
        builder.Services.AddSingleton<BonjourPeerDiscovery>();
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
        builder.Services.AddTransient<PeerPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<FolderPickerPage>();
        builder.Services.AddTransient<TagsPage>();
        builder.Services.AddTransient<TagArticlesPage>();
        return builder.Build();
    }
}
