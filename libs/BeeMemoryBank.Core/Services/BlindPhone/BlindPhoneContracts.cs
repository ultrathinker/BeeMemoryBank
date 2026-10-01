using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services.BlindPhone;

// The seams of the Android blind node (plan section 10). The logic in this folder is platform-free
// and tested; the phone app supplies the Android side (Preferences, Keystore, WorkManager). Pieces
// that need parts of the blind-node work not wired to the phone yet are interfaces here with a TODO
// naming the missing part.

/// <summary>Small persistent key/value state (Preferences on the phone).</summary>
public interface IBlindPhoneStore
{
    string? Get(string key);
    void Set(string key, string? value);
}

/// <summary>
/// The phone's secrets, kept outside any DEK (plan 3.5): the Ed25519 identity seed, the backup key and
/// the one-time pairing secret. On Android each is wrapped by a non-exportable AndroidKeyStore key.
/// </summary>
public interface IBlindPhoneKeys
{
    void SaveIdentitySeed(byte[] seed);
    /// <summary>The Ed25519 identity seed, or null if none. Caller clears it.</summary>
    byte[]? LoadIdentitySeed() => null;
    void SaveBackupKey(byte[] key);
    /// <summary>The backup key, or null if none. Caller clears it.</summary>
    byte[]? LoadBackupKey();
    void SavePairingSecret(byte[] secret);
    /// <summary>The pairing secret, or null when none is waiting for an answer. Caller clears it.</summary>
    byte[]? LoadPairingSecret();
    /// <summary>Spends the pairing secret (an answer was accepted).</summary>
    void ClearPairingSecret();
    /// <summary>Forgets every secret and its Keystore key.</summary>
    void Clear();
}

public sealed record BlindIdentityRecord(Guid NodeId, byte[] PublicKey, string DisplayName);

/// <summary>
/// Records the phone's identity row. TODO(identity row v=2): write <c>tbl_node_identity</c> with
/// <c>ed25519_private_key_v = 2</c> (key outside the DEK).
/// </summary>
public interface IBlindIdentityRecorder
{
    Task RecordAsync(Guid nodeId, byte[] publicKey, string displayName, CancellationToken ct);
    Task<BlindIdentityRecord?> GetRecordedAsync(CancellationToken ct = default) => Task.FromResult<BlindIdentityRecord?>(null);
    Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// The first load: downloads the blind package from the listening node — resumably, the partial
/// download kept in <c>workDirectory</c> — and installs it as the phone's data.
/// TODO(GET /api/blind/replica + the blind package reader on the phone): sync-token auth
/// of the whitelisted blind node, pinned by <see cref="BlindCallCode.SpkiPin"/>, package signature
/// checked against <see cref="BlindCallCode.PublicKey"/>.
/// </summary>
public interface IBlindReplicaSource
{
    Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// One sync round with the listening node. TODO(identity v=2 signer + protocol 3 on the phone):
/// SyncClient with the file/Keystore key signer.
/// </summary>
public interface IBlindPhoneSync
{
    Task SyncOnceAsync(BlindCallCode target, CancellationToken ct);
}

/// <summary>
/// Writes the body of a backup (<see cref="BlindPhoneBackupBody"/>): the blind package the listening node
/// signed (the replica, <c>GET /api/blind/replica</c>, with its signature), and the signed events the phone
/// holds. TODO(the replica with its signature, stored on the phone).
/// </summary>
public interface IBlindPackageSource
{
    Task CreateAsync(string destinationPath, CancellationToken ct);
}

/// <summary>
/// The recovery set of the phone's data — the open header of a backup. TODO(RecoverySetBuilder over the
/// phone's database): <c>RecoverySetBuilder.BuildJsonAsync()</c>.
/// </summary>
public interface IRecoverySetJsonSource
{
    Task<string> BuildJsonAsync(CancellationToken ct);
}

/// <summary>Network, charger and battery, as the phone sees them now.</summary>
public interface IDeviceStateProvider
{
    BlindPhoneDeviceState Current();
}

/// <summary>A piece not wired to the phone yet (see the TODOs above).</summary>
public sealed class BlindFeaturePendingException(string contractItem)
    : InvalidOperationException($"Not available yet: waiting for {contractItem}.")
{
    public string ContractItem { get; } = contractItem;
}
