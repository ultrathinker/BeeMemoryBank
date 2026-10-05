using BeeMemoryBank.BlindMobile.Pages;
using BeeMemoryBank.BlindMobile.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.Logging;
#if ANDROID
using BeeMemoryBank.BlindMobile.Platforms.Android;
#endif

namespace BeeMemoryBank.BlindMobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if ANDROID
        // Disable autofill on every Entry
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("BmbNoAutofill", (handler, view) =>
        {
            var editText = handler.PlatformView;
            if (editText is null) return;
            editText.ImportantForAutofill = Android.Views.ImportantForAutofill.No;
            editText.SetAutofillHints((string[]?)null);
        });
#endif

        var dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(dataDir, "beememorybank.db");

        // The page asks the shared controller to make the identity; the device name is the phone's (Android's own part of it).
        BlindMobileServices.AddBlindAppCore(builder.Services, new BlindAppOptions(dataDir, dbPath, DisplayNameFactory: () => DeviceInfo.Current.Name));

#if ANDROID
        builder.Services
            .AddSingleton<KeystoreBlindPhoneKeys>()
            .AddSingleton<IBlindNodeKeys>(sp => sp.GetRequiredService<KeystoreBlindPhoneKeys>())
            .AddSingleton<IBlindSecretStore>(sp => sp.GetRequiredService<KeystoreBlindPhoneKeys>())
            .AddSingleton<IBlindPhoneKeys>(sp => sp.GetRequiredService<KeystoreBlindPhoneKeys>())
            .AddSingleton<IBlindStateStore, PreferencesBlindStore>()
            .AddSingleton<IBlindLifecycle, AndroidBlindLifecycle>();
#endif

        builder.Services.AddTransient<BlindHomePage>();

        return builder.Build();
    }

    public static IServiceCollection ConfigureServices(IServiceCollection services, string dataDir, string dbPath) =>
        BlindMobileServices.ConfigureServices(services, dataDir, dbPath);
}
