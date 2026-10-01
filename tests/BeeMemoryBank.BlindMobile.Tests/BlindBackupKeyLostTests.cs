using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The backup key is made once, at pairing, and the computer holds its sealed copy: a new one made silently would
/// write backups the computer cannot open. When the Keystore no longer has it, the phone says so on its screen —
/// disconnect and pair again — instead of leaving the user to find a log line (stage 2 rule, shown in stage 4).
/// </summary>
public sealed class BlindBackupKeyLostTests
{
    [Fact]
    public void AnIdentityWithoutItsBackupKey_IsLost()
    {
        var (pairing, keys, state) = Rig(withIdentity: true, withKey: false);

        pairing.BackupKeyLost.Should().BeTrue();
        keys.BackupKeyLoads.Should().Be(1, "it asked the Keystore, it did not guess");
        keys.SavedBackupKeys.Should().Be(0, "a lost key is never replaced silently");
        state.NodeId.Should().NotBeNull();
    }

    [Fact]
    public void AnIdentityWithItsBackupKey_IsNotLost_AndTheLoadedBufferIsCleared()
    {
        var (pairing, keys, _) = Rig(withIdentity: true, withKey: true);

        pairing.BackupKeyLost.Should().BeFalse();

        keys.HandedOut.Should().ContainSingle().Which.Should().OnlyContain(b => b == 0, "the caller clears what the Keystore hands out");
    }

    [Fact]
    public void AFreshApp_WithNoIdentityYet_HasNothingToLose()
    {
        var (pairing, keys, _) = Rig(withIdentity: false, withKey: false);

        pairing.BackupKeyLost.Should().BeFalse();
        keys.BackupKeyLoads.Should().Be(0);
    }

    private static (BlindMobilePairing Pairing, CountingKeys Keys, BlindPhoneState State) Rig(bool withIdentity, bool withKey)
    {
        var state = new BlindPhoneState(new MemoryStore());
        if (withIdentity) state.NodeId = Guid.NewGuid();
        var keys = new CountingKeys(withKey);
        var recorder = new SqliteBlindIdentityRecorder(new DbConnectionFactory(Path.Combine(Path.GetTempPath(), "bmb-s4-keylost-" + Guid.NewGuid().ToString("N") + ".db")));
        var log = new BlindPhoneLog(Path.Combine(Path.GetTempPath(), "bmb-s4-keylost-" + Guid.NewGuid().ToString("N") + ".jsonl"), TimeProvider.System);
        return (new BlindMobilePairing(state, keys, recorder, log), keys, state);
    }

    private sealed class MemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private sealed class CountingKeys(bool hasBackupKey) : IBlindNodeKeys
    {
        public int BackupKeyLoads { get; private set; }
        public int SavedBackupKeys { get; private set; }
        public List<byte[]> HandedOut { get; } = [];

        public byte[]? LoadBackupKey()
        {
            BackupKeyLoads++;
            if (!hasBackupKey) return null;
            var key = Enumerable.Repeat((byte)7, 32).ToArray();
            HandedOut.Add(key);
            return key;
        }

        public void SaveBackupKey(byte[] key) => SavedBackupKeys++;
        public void SaveIdentitySeed(byte[] seed) { }
        public byte[]? LoadIdentitySeed() => null;
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }
}
