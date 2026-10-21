using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>Creates the Windows implementations of the AppCore seams (the ones that depend on the operating system).</summary>
public static class WindowsBlindPlatform
{
    /// <summary>Per-account secrets (DPAPI) and state (atomic JSON file), the device conditions (network cost, Energy Saver) and autostart (HKCU Run) for the app root <paramref name="paths"/>.</summary>
    public static WindowsBlindSeams Create(WindowsBlindPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new WindowsBlindSeams(
            paths, new WindowsSecretStoreFactory(paths.Root), new WindowsStateStoreFactory(paths.Root), new WindowsDeviceConditions(), new RegistryAutostart());
    }
}

/// <summary>The seams of the Windows head. The seams that are the same on every desktop - lifecycle, backup export picker, notifications - belong to the host.</summary>
public sealed record WindowsBlindSeams(
    WindowsBlindPaths Paths, IBlindSecretStoreFactory Secrets, IBlindStateStoreFactory States, IBlindDeviceConditions Conditions, IBlindAutostart Autostart);
