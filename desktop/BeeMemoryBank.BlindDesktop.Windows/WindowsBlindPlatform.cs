using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>Registers the Windows implementations of the AppCore seams (the ones that depend on the operating system).</summary>
public static class WindowsBlindPlatform
{
    /// <summary>
    /// Paths, secrets (DPAPI), state (atomic JSON file), device conditions (AC power, network cost) and autostart (HKCU Run).
    /// The seams that are the same on every desktop - lifecycle, backup export picker, notifications - belong to the host.
    /// </summary>
    public static IServiceCollection AddWindowsBlindSeams(this IServiceCollection services, WindowsBlindPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        services.TryAddSingleton<IBlindPaths>(paths);
        services.TryAddSingleton(paths);
        services.TryAddSingleton<IBlindSecretStore>(new DpapiSecretStore(paths.SecretsDirectory));
        services.TryAddSingleton<IBlindStateStore>(new AtomicJsonStateStore(paths.StatePath));
        services.TryAddSingleton<IBlindAutostart>(_ => new RegistryAutostart());
        return services;
    }
}
