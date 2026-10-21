using BeeMemoryBank.BlindMobile.Pages;
using BeeMemoryBank.BlindMobile.Services.Blind;
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

        // Every dialog and result the shared busy helper shows for a failed action goes through the closed failure text of the blind core.
        BlindFailureSetup.Apply();

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

#if ANDROID
        // ONE device object: the account host (every account is the single-mesh blind app of 2.6, composed by AppCore once more) and the two schedulers. The 2.6 install in the root is account 1,
        // under the names it always had. The pages and the workers reach the accounts through it and nothing else; the device name is the phone's.
        builder.Services
            .AddSingleton(_ => new BlindDevice(BlindAndroidHead.HostOptions(
                dataDir, new AndroidKeystoreKeyWrap(), PreferencesBlindStore.ForFile, AndroidDeviceConditions.Create(Platform.AppContext),
                displayNameFactory: () => DeviceInfo.Current.Name, lifecycle: new AndroidAccountLifecycle())))
            .AddSingleton<IBlindAccountsController>(sp => sp.GetRequiredService<BlindDevice>().Host);
#if BMB_QR_SCANNER
        // "Scan a QR code" on the Add page: the system's scanner (Google Play services), no camera permission of ours. Built only with -p:BmbQrScanner=true (see the project file).
        builder.Services.AddSingleton<IBlindQrScanner, AndroidQrScanner>();
#endif
#endif

        builder.Services.AddTransient<BlindAccountsPage>();
        builder.Services.AddTransient<BlindAddPage>();

        return builder.Build();
    }
}
