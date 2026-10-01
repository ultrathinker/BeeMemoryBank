using BeeMemoryBank.BlindMobile.Pages;
using BeeMemoryBank.BlindMobile.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage;
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

        builder.Services
            .AddStorage(dbPath)
            .AddCore()
            .AddLogging();

        builder.Services.AddTransient<MaintenanceDetectingHandler>();
        builder.Services.AddTransient<BlindHttpHandler>();
        builder.Services.AddSingleton<BlindHttpClientProvider>();
        builder.Services.AddSingleton<IHttpClientFactory>(sp => sp.GetRequiredService<BlindHttpClientProvider>());
        builder.Services.AddTransient<HttpClient>(sp => sp.GetRequiredService<BlindHttpClientProvider>().GetClient());

        builder.Services
            .AddSingleton<IBlindPhoneStore, PreferencesBlindStore>()
            .AddSingleton<BlindPhoneState>()
            .AddSingleton(_ => new BlindPhoneLog(BlindPaths.Log(dataDir), TimeProvider.System))
            .AddSingleton<IBlindIdentityRecorder, PendingBlindIdentityRecorder>()
            .AddSingleton<IBlindReplicaSource, PendingBlindReplicaSource>()
            .AddSingleton<IBlindPhoneSync, PendingBlindPhoneSync>()
            .AddSingleton<IBlindPackageSource, PendingBlindPackageSource>()
            .AddSingleton<IRecoverySetJsonSource, PendingRecoverySetSource>()
            .AddSingleton<BlindPhonePairing>()
            .AddSingleton(sp => new BlindPhoneBackupRunner(
                sp.GetRequiredService<BlindPhoneState>(),
                sp.GetRequiredService<IBlindPhoneKeys>(),
                sp.GetRequiredService<IBlindPackageSource>(),
                sp.GetRequiredService<IRecoverySetJsonSource>(),
                sp.GetRequiredService<IDeviceStateProvider>(),
                sp.GetRequiredService<BlindPhoneLog>(),
                BlindPaths.Backups(dataDir), TimeProvider.System))
            .AddSingleton(sp => new BlindHeavyWork(
                sp.GetRequiredService<BlindPhoneState>(),
                sp.GetRequiredService<IBlindReplicaSource>(),
                sp.GetRequiredService<BlindPhoneBackupRunner>(),
                sp.GetRequiredService<IDeviceStateProvider>(),
                sp.GetRequiredService<BlindPhoneLog>(),
                BlindPaths.Replica(dataDir), TimeProvider.System));

#if ANDROID
        builder.Services
            .AddSingleton<IBlindPhoneKeys, KeystoreBlindPhoneKeys>()
            .AddSingleton<IDeviceStateProvider, AndroidDeviceState>();
#endif

        builder.Services.AddTransient<BlindHomePage>();

        return builder.Build();
    }
}
