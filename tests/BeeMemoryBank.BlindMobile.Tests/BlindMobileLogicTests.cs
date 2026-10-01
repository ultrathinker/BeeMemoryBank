using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

public class BlindMobileLogicTests
{
    [Fact]
    public void BlindPaths_FormatPathsCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb_test_" + Guid.NewGuid().ToString("N"));

        BlindPaths.Backups(tempDir).Should().Be(Path.Combine(tempDir, "blind-backups"));
        BlindPaths.Replica(tempDir).Should().Be(Path.Combine(tempDir, "blind-replica"));
        BlindPaths.Log(tempDir).Should().Be(Path.Combine(tempDir, "blind-log.jsonl"));
    }

    [Fact]
    public async Task PendingBlindIdentityRecorder_RecordAsync_CompletesSuccessfully()
    {
        var recorder = new PendingBlindIdentityRecorder();
        var nodeId = Guid.NewGuid();
        var pubKey = new byte[32];

        var task = recorder.RecordAsync(nodeId, pubKey, "TestPhone", CancellationToken.None);
        await task;

        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task PendingBlindReplicaSource_ThrowsBlindFeaturePendingException()
    {
        var source = new PendingBlindReplicaSource();
        var callCode = new BlindCallCode("https://127.0.0.1:5300", Guid.NewGuid(), "test_pin", new byte[32], new byte[32]);

        var act = () => source.FetchAndInstallAsync(callCode, "workDir", null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("GET /api/blind/replica");
    }

    [Fact]
    public async Task PendingBlindPhoneSync_ThrowsBlindFeaturePendingException()
    {
        var sync = new PendingBlindPhoneSync();
        var callCode = new BlindCallCode("https://127.0.0.1:5300", Guid.NewGuid(), "test_pin", new byte[32], new byte[32]);

        var act = () => sync.SyncOnceAsync(callCode, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("v=2 identity signer");
    }

    [Fact]
    public async Task PendingBlindPackageSource_ThrowsBlindFeaturePendingException()
    {
        var pkg = new PendingBlindPackageSource();

        var act = () => pkg.CreateAsync("dest.tar.gz", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("building the blind package");
    }

    [Fact]
    public async Task PendingRecoverySetSource_ThrowsBlindFeaturePendingException()
    {
        var recovery = new PendingRecoverySetSource();

        var act = () => recovery.BuildJsonAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("the recovery set of the phone's data");
    }

    private sealed class InMemoryBlindPhoneStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Get(string key) => _store.TryGetValue(key, out var val) ? val : null;
        public void Set(string key, string? value)
        {
            if (value is null) _store.Remove(key);
            else _store[key] = value;
        }
    }

    private sealed class InMemoryBlindPhoneKeys : IBlindPhoneKeys
    {
        public byte[]? IdentitySeed { get; private set; }
        public byte[]? BackupKey { get; private set; }
        public byte[]? PairingSecret { get; private set; }

        public void SaveIdentitySeed(byte[] seed) => IdentitySeed = (byte[])seed.Clone();
        public void SaveBackupKey(byte[] key) => BackupKey = (byte[])key.Clone();
        public byte[]? LoadBackupKey() => BackupKey != null ? (byte[])BackupKey.Clone() : null;
        public void SavePairingSecret(byte[] secret) => PairingSecret = (byte[])secret.Clone();
        public byte[]? LoadPairingSecret() => PairingSecret != null ? (byte[])PairingSecret.Clone() : null;
        public void ClearPairingSecret() => PairingSecret = null;
        public void Clear()
        {
            IdentitySeed = null;
            BackupKey = null;
            PairingSecret = null;
        }
    }

    [Fact]
    public void BlindPhoneReset_WipeAndRestart_ClearsStateAndKeys()
    {
        var services = new ServiceCollection();
        var keys = new InMemoryBlindPhoneKeys();
        var store = new InMemoryBlindPhoneStore();
        var state = new BlindPhoneState(store);

        keys.SaveBackupKey(new byte[] { 1, 2, 3 });
        keys.SavePairingSecret(new byte[] { 4, 5, 6 });
        state.NodeId = Guid.NewGuid();
        state.DisplayName = "WipeTestPhone";

        services.AddSingleton<IBlindPhoneKeys>(keys);
        services.AddSingleton(state);

        var provider = services.BuildServiceProvider();

        // Calling WipeAndRestart on non-Android platform executes the cleanup logic
        BlindPhoneReset.WipeAndRestart(provider);

        keys.LoadBackupKey().Should().BeNull();
        keys.LoadPairingSecret().Should().BeNull();
        state.NodeId.Should().BeNull();
        state.DisplayName.Should().BeNull();
    }
}
