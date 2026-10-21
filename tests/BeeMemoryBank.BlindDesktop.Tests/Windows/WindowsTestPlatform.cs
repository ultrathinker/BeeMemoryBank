using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>
/// The real Windows platform (the paths, the DPAPI and state-file factories) with the two things a test must not touch replaced: autostart (the real Run key) and the device's conditions
/// (a wired, plugged-in computer).
/// </summary>
internal sealed class WindowsTestPlatform(IBlindDesktopPlatform inner, IBlindSecretStoreFactory? secretStores = null) : IBlindDesktopPlatform
{
    public string Name => inner.Name;
    public IBlindPaths Paths => inner.Paths;
    public IBlindSecretStoreFactory SecretStores => secretStores ?? inner.SecretStores;
    public IBlindStateStoreFactory StateStores => inner.StateStores;
    public IBlindDeviceConditions DeviceConditions { get; } = new FakeConditions { IsMetered = false, IsPowerSaving = false };
    public IBlindAutostart Autostart { get; } = new FakeAutostart();
    public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
    public bool SignalRunningInstance() => inner.SignalRunningInstance();
}
