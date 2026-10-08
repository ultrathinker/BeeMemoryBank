using BeeMemoryBank.BlindIos.Pages;
using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;

namespace BeeMemoryBank.BlindIos;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<IosBlindBackground>();
        builder.Services.AddSingleton<IIosBackgroundRequests>(sp => sp.GetRequiredService<IosBlindBackground>());
        builder.Services.AddSingleton<IosSilenceNotifier>();
        // The blind copy itself lives in a composition of its own that "Disconnect and wipe" replaces (IosBlindHost); this container only
        // holds the app's shell around it. The device name iOS gives an app is the model ("iPhone"), which is what the computer lists.
        builder.Services.AddSingleton(sp => new IosBlindHost(returnToFirstRun => IosBlindRuntime.Create(new IosBlindRuntimeOptions(
            IosContainer.Prepare(),
            () => new IosKeychainSecretStore(new SecKeychainBackend(IosKeychainSecretStore.DefaultService)),
            sp.GetRequiredService<IIosBackgroundRequests>(),
            DisplayName: () => DeviceInfo.Current.Name), returnToFirstRun)));

        builder.Services.AddTransient<BlindHomePage>();
        return builder.Build();
    }
}
