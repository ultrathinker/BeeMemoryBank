using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>What the host can choose about the macOS adapters. The defaults are the production ones; everything else is for tests and tools.</summary>
public sealed class MacOsBlindHostOptions
{
    private readonly object _gate = new();
    private MacOsBlindPaths? _paths;

    /// <summary>The folder that contains the app's data folder. Null: <c>~/Library/Application Support</c>.</summary>
    public string? ApplicationSupportRoot { get; init; }

    /// <summary>
    /// The data folder itself, at exactly this place (<c>--data-dir</c>: a scratch folder for a check or a test). Null: the default
    /// <c>BeeMemoryBankBlind</c> folder under <see cref="ApplicationSupportRoot"/>. Takes precedence over <see cref="ApplicationSupportRoot"/>.
    /// </summary>
    public string? DataDirectory { get; init; }

    /// <summary>The Keychain service name the secrets are filed under.</summary>
    public string KeychainService { get; init; } = MacOsKeychainSecretStore.DefaultService;

    /// <summary>
    /// A keychain FILE to confine the secrets to. Null: the user's default keychain (production). Only tests and tools set this, and
    /// they never point it at the login keychain.
    /// </summary>
    public string? KeychainFile { get; init; }

    public string AutostartLabel { get; init; } = MacOsBlindAutostartOptions.DefaultLabel;
    public string? LaunchAgentsDirectory { get; init; }
    public IReadOnlyList<string>? AutostartProgramArguments { get; init; }
    public bool LoadAutostartImmediately { get; init; } = true;

    /// <summary>For tests: a Keychain backend that replaces Security.framework.</summary>
    internal IKeychainBackend? KeychainBackend { get; init; }

    /// <summary>
    /// The app's data folder, created (mode 0700) the first time it is asked for. Read it to build the options of
    /// <c>BlindMobileServices.AddBlindAppCore</c> from the same folder.
    /// </summary>
    public MacOsBlindPaths Paths
    {
        get
        {
            lock (_gate)
                return _paths ??= DataDirectory is { } exact ? MacOsBlindPaths.AtDirectory(exact) : new MacOsBlindPaths(ApplicationSupportRoot);
        }
    }
}

/// <summary>The seams of the macOS head for a device that holds several accounts: the stores are factories, one set of items per account.</summary>
public sealed record MacOsBlindSeams(
    MacOsBlindPaths Paths, IBlindSecretStoreFactory Secrets, IBlindStateStoreFactory States, IBlindDeviceConditions Conditions, IBlindAutostart Autostart);

/// <summary>Registers the macOS implementations of the Blind.AppCore host seams.</summary>
public static class MacOsBlindHostServices
{
    /// <summary>
    /// The seams for the account host: per-account Keychain items (<see cref="MacOsSecretStoreFactory"/>) and state files (<see cref="MacOsBlindStateStoreFactory"/>), the device conditions
    /// (Network.framework path, Low Power Mode) and the LaunchAgent. Nothing native is touched until a seam is first used.
    /// </summary>
    /// <param name="secrets">Replaces the Keychain factory (the tests of the host pass an in-memory one so that they never touch a keychain).</param>
    public static MacOsBlindSeams CreateSeams(MacOsBlindHostOptions? options = null, IBlindSecretStoreFactory? secrets = null, IBlindDeviceConditions? conditions = null)
    {
        options ??= new MacOsBlindHostOptions();
        var paths = options.Paths;
        IBlindSecretStoreFactory secretFactory = secrets
            ?? (options.KeychainBackend is { } backend ? new MacOsSecretStoreFactory(backend, options.KeychainService)
                : options.KeychainFile is { } file ? new MacOsSecretStoreFactory(file, options.KeychainService)
                : new MacOsSecretStoreFactory(options.KeychainService));
        return new MacOsBlindSeams(
            paths, secretFactory, new MacOsBlindStateStoreFactory(paths.DataDirectory), conditions ?? new MacOsDeviceConditions(),
            new MacOsBlindAutostart(new MacOsBlindAutostartOptions
            {
                Label = options.AutostartLabel,
                LaunchAgentsDirectory = options.LaunchAgentsDirectory,
                ProgramArguments = options.AutostartProgramArguments,
                LoadImmediately = options.LoadAutostartImmediately,
            }));
    }

    /// <summary>
    /// Registers, as singletons and only where the host has not registered its own, the seams that depend on macOS: <see cref="IBlindPaths"/>,
    /// <see cref="IBlindSecretStore"/> (Keychain), <see cref="IBlindStateStore"/> (atomic file) and <see cref="IBlindAutostart"/>
    /// (LaunchAgent), and each concrete type too (for <c>MacOsBlindAutostart.IsEnabled</c>). The host (the Avalonia app) brings its own
    /// lifecycle, notifications, scheduler and backup exporter. Nothing native is touched until a seam is first used.
    /// </summary>
    public static IServiceCollection AddMacOsBlindSeams(this IServiceCollection services, MacOsBlindHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new MacOsBlindHostOptions();

        services.TryAddSingleton<IBlindPaths>(_ => options.Paths);

        services.TryAddSingleton<MacOsKeychainSecretStore>(_ =>
            options.KeychainBackend is { } backend ? new MacOsKeychainSecretStore(backend, options.KeychainService)
            : options.KeychainFile is { } file ? new MacOsKeychainSecretStore(file, options.KeychainService)
            : new MacOsKeychainSecretStore(options.KeychainService));
        services.TryAddSingleton<IBlindSecretStore>(sp => sp.GetRequiredService<MacOsKeychainSecretStore>());

        services.TryAddSingleton<IBlindStateStore>(sp => new MacOsBlindStateStore(sp.GetRequiredService<IBlindPaths>()));

        services.TryAddSingleton<MacOsBlindAutostart>(_ => new MacOsBlindAutostart(new MacOsBlindAutostartOptions
        {
            Label = options.AutostartLabel,
            LaunchAgentsDirectory = options.LaunchAgentsDirectory,
            ProgramArguments = options.AutostartProgramArguments,
            LoadImmediately = options.LoadAutostartImmediately,
        }));
        services.TryAddSingleton<IBlindAutostart>(sp => sp.GetRequiredService<MacOsBlindAutostart>());

        return services;
    }
}
