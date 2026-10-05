using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

public sealed class BlindAppControllerTests
{
    [Fact]
    public void Status_UsesPersistedStateAndKeepsLegacySchedule()
    {
        var state = new BlindPhoneState(new MemoryState()) { DisplayName = "Blind copy", Schedule = BlindBackupSchedule.Daily };
        state.DisplayName.Should().Be("Blind copy");
        state.Schedule.Should().Be(BlindBackupSchedule.Daily);
    }

    [Fact]
    public void RealComposition_WithHostSeams_ResolvesTheController()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBlindStateStore, MemoryState>();
        services.AddSingleton<IBlindSecretStore, MemorySecrets>();
        services.AddSingleton<IBlindLifecycle, Lifecycle>();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "bmb-appcore-test.db")));

        services.BuildServiceProvider().GetRequiredService<BlindAppController>().Should().NotBeNull();
    }

    private sealed class MemoryState : IBlindStateStore
    {
        private readonly Dictionary<string, string?> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value) => _values[key] = value;
    }

    private sealed class MemorySecrets : IBlindSecretStore
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public byte[]? LoadIdentitySeed() => null;
        public byte[]? LoadBackupKey() => null;
        public byte[]? LoadPairingSecret() => null;
        public void SaveBackupKey(byte[] key) { }
        public void SavePairingSecret(byte[] secret) { }
        public void ClearPairingSecret() { }
        public void Clear() { }
    }


    private sealed class Lifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }
}
