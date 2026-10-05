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

/// <summary>Registers the macOS implementations of the Blind.AppCore host seams.</summary>
public static class MacOsBlindHostServices
{
    /// <summary>
    /// Registers, as singletons and only where the host has not registered its own: <see cref="IBlindPaths"/>, <see cref="IBlindSecretStore"/>
    /// (Keychain), <see cref="IBlindStateStore"/> (atomic file),
    /// <see cref="IBlindAutostart"/> (LaunchAgent), <see cref="IBlindNotifications"/> and <see cref="IBlindLifecycle"/>, and each concrete
    /// type too (for <c>MacOsBlindAutostart.IsEnabled</c>, <c>MacOsBlindLifecycle.RestartRequested</c>, the conditions' reasons).
    /// Not registered: <see cref="IBlindScheduler"/> (the host's in-process timer loop; the lifecycle uses it when it is there) and
    /// <see cref="IBlindBackupExporter"/> (the host's save-file picker). Nothing native is touched until a seam is first used.
    /// </summary>
    public static IServiceCollection AddMacOsBlindHost(this IServiceCollection services, MacOsBlindHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new MacOsBlindHostOptions();
        services.AddMacOsBlindSeams(options);

        services.TryAddSingleton<MacOsBlindNotifications>();
        services.TryAddSingleton<IBlindNotifications>(sp => sp.GetRequiredService<MacOsBlindNotifications>());

        services.TryAddSingleton<MacOsBlindLifecycle>(sp => new MacOsBlindLifecycle(sp.GetService<IBlindScheduler>(), sp.GetService<IBlindStateStore>() as MacOsBlindStateStore));
        services.TryAddSingleton<IBlindLifecycle>(sp => sp.GetRequiredService<MacOsBlindLifecycle>());

        return services;
    }

    /// <summary>
    /// Only the seams that depend on macOS and that every macOS host needs: <see cref="IBlindPaths"/>, <see cref="IBlindSecretStore"/> (Keychain),
    /// <see cref="IBlindStateStore"/> and <see cref="IBlindAutostart"/>. A host that has its own
    /// lifecycle, notifications and scheduler (the Avalonia app) uses this one; <see cref="AddMacOsBlindHost"/> adds the lifecycle and the
    /// notifier to it. Nothing native is touched until a seam is first used.
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

    /// <summary>The adapters above plus Blind.AppCore's composition, both on the same data folder.</summary>
    public static IServiceCollection AddMacOsBlindApp(this IServiceCollection services, MacOsBlindHostOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new MacOsBlindHostOptions();
        services.AddMacOsBlindHost(options);
        return BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(options.Paths.DataDirectory, options.Paths.DatabasePath, time));
    }
}
