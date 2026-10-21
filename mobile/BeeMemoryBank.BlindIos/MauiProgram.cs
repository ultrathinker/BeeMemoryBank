using BeeMemoryBank.BlindIos.Pages;
using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;

namespace BeeMemoryBank.BlindIos;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Every dialog and result the shared busy helper shows for a failed action goes through the closed failure text of the iPhone head: set first, before anything can run an action.
        IosFailureSetup.Apply();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<IosBlindBackground>();
        builder.Services.AddSingleton<IIosBackgroundRequests>(sp => sp.GetRequiredService<IosBlindBackground>());
        builder.Services.AddSingleton<IosSilenceNotifier>();
        builder.Services.AddSingleton<IIosFileProtection, IosFileAttributes>();
        // What the phone says about its network (NWPathMonitor) and its power (Low Power Mode): the answers to the two switches of an account.
        builder.Services.AddSingleton<IIosNetworkPathSource, IosNetworkPathMonitor>();
        builder.Services.AddSingleton<IIosPowerSource, IosPowerSource>();
        // The blind app itself lives in ONE composition for the life of the process: the host with the accounts (each a complete copy of its own: folder, database, identity, Keychain
        // items, state, pinned HTTP client) and the one loop that serves them. Adding or removing an account builds or ends that account only; nothing else is rebuilt. This container only
        // holds the app's shell around it. The device name iOS gives an app is the model ("iPhone"), which is what the computer lists.
        builder.Services.AddSingleton(sp =>
        {
            var files = sp.GetRequiredService<IIosFileProtection>();
            return IosBlindRuntime.Create(new IosBlindRuntimeOptions(
                IosContainer.Prepare(files),
                new IosKeychainSecretStoreFactory(new SecKeychainBackend(IosKeychainSecretStore.DefaultService)),
                sp.GetRequiredService<IIosBackgroundRequests>(),
                new IosDeviceConditions(sp.GetRequiredService<IIosNetworkPathSource>(), sp.GetRequiredService<IIosPowerSource>()),
                files,
                DisplayName: () => DeviceInfo.Current.Name));
        });

        builder.Services.AddTransient<AccountsPage>();
        return builder.Build();
    }
}
