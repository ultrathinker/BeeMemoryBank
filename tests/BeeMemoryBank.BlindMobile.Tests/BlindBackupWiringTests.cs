using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>Stage 4: the backup runner is built from the real package and recovery-set sources, not stand-ins.</summary>
public sealed class BlindBackupWiringTests
{
    [Fact]
    public void TheAppContainer_BuildsTheBackupRunnerFromTheRealSources()
    {
        using var provider = BuildProvider(out _);

        provider.GetRequiredService<IBlindPackageSource>().Should().BeOfType<BlindPhonePackageSource>(
            "a stand-in would answer every backup with 'not available yet'");
        provider.GetRequiredService<IRecoverySetJsonSource>().Should().BeOfType<BlindPhoneRecoverySetSource>();
        provider.GetRequiredService<IBlindVerifiedPackageFetcher>().Should().BeOfType<BlindPackageFetcher>();
        provider.GetRequiredService<BlindPhoneBackupRunner>().Should().NotBeNull();
    }

    [Fact]
    public async Task TheFetcher_OfAnUnpairedPhone_FailsClosedAndCallsNobody()
    {
        using var provider = BuildProvider(out _);
        var fetcher = provider.GetRequiredService<IBlindVerifiedPackageFetcher>();
        provider.GetRequiredService<BlindPhoneState>().CallCode.Should().BeNull();

        var act = () => fetcher.FetchAsync(_ => { }, progress: null, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("not paired");
    }

    private static ServiceProvider BuildProvider(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bmb-s4-wiring-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        BlindMobileServices.ConfigureServices(services, dir, Path.Combine(dir, "beememorybank.db"));
        var keys = new NoKeys();
        services.AddSingleton<IBlindStateStore>(new PreferencesBlindStore(_ => null, (_, _) => { }, _ => { }));
        services.AddSingleton<IBlindNodeKeys>(keys);
        services.AddSingleton<IBlindSecretStore>(keys);
        services.AddSingleton<IBlindLifecycle, TestLifecycle>();
        return services.BuildServiceProvider();
    }

    private sealed class NoKeys : IBlindNodeKeys
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public byte[]? LoadIdentitySeed() => null;
        public void SaveBackupKey(byte[] key) { }
        public byte[]? LoadBackupKey() => null;
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

}
