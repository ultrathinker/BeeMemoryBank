using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>A door about to be written to the sealed database (docs/design/step-b-spec.md 4.3 rule 1). Same fields as BootDoor.</summary>
public sealed record PendingDoor(
    string Kind,
    long SourceId,
    string? SlotType,
    byte[] EncryptedMasterDek,
    byte[] Iv,
    byte[]? Salt,
    int? ArgonMemory,
    int? ArgonIterations,
    int? ArgonParallelism,
    string? KeyHash,
    int? KdfVersion,
    string? DekFingerprint);

/// <summary>
/// Keeps the boot store's copies (doors, DBK wrappers, identity and whitelist mirrors) in step with the sealed database (spec 4.3).
/// Track 2. Every member is a no-op on a plain node.
/// </summary>
public interface IUnlockMaterialMirror
{
    /// <summary>Rewrites every copy from the open database in one boot transaction and prunes DBK wrappers (spec 3.4, 4.3 rule 3).</summary>
    Task RewriteAsync(CancellationToken ct = default);

    /// <summary>Before a commit that adds or replaces a door (rule 1).</summary>
    Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default);

    /// <summary>Before a commit that removes a door (rule 2).</summary>
    Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default);

    /// <summary>Before a DEK rotation or a restore commits: a DBK wrapper under the master DEK that will be current (spec 3.5).</summary>
    Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>
    /// Writes a complete boot store for a database that is not the live one yet (migration target, re-key target, spec 10.4):
    /// identity, whitelist, doors read from <paramref name="vault"/>, the DBK wrapped under every key of <paramref name="masterDeks"/>.
    /// </summary>
    Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
        IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default);
}

/// <summary>The database key (spec 3). Track 2.</summary>
public interface IDatabaseKeyService
{
    /// <summary>A fresh 32-byte DBK from the CSPRNG; the caller clears it.</summary>
    byte[] Create();

    /// <summary>The DBK of the current generation through the boot wrapper of <paramref name="masterDek"/>'s fingerprint; null when none opens.</summary>
    Task<byte[]?> TryUnwrapAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>HKDF-SHA256 page key of one file (spec 3.2); the caller clears it.</summary>
    byte[] DerivePageKey(ReadOnlySpan<byte> databaseKey, string purpose);
}

/// <summary>The open vault's database key, for the mirror's wrappers (implemented by the gate, track 3).</summary>
public interface ISealedVaultKeys
{
    /// <summary>A copy of the DBK; throws VaultSealedException when closed. The caller clears it.</summary>
    byte[] GetDatabaseKey();

    int DbkGeneration { get; }
}

public sealed record RelayImportResult(int Applied, int Held, int AlreadyPresent, int Refused, int Waiting);

/// <summary>The relay inbox's part of the open and close sequences (spec 5.3, 5.4, 6.3, 6.6). Track 2; called by the gate (track 3).</summary>
public interface ISealedRelayLifecycle
{
    /// <summary>Raises tbl_event's sequence above every relay sequence and sets the open floor (spec 6.3). Before anything appends.</summary>
    Task OnOpenedAsync(CancellationToken ct = default);

    /// <summary>Replays the inbox in relay order through EventApplier (spec 6.6); safe to call again; removes what it applied.</summary>
    Task<RelayImportResult> ImportAsync(CancellationToken ct = default);

    /// <summary>Records the clean-close head and floor (spec 6.3), while the database can still be read.</summary>
    Task OnClosingAsync(CancellationToken ct = default);

    /// <summary>
    /// Pauses every write of the relay (receipts, position reports, blobs) for the seconds a file copy takes (spec 6.8): waits for the
    /// receipts in flight, then the write routes answer 503 <c>vault_sealed_busy</c> until the lease is disposed. Granted only while the
    /// gate is Closed; otherwise throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<IAsyncDisposable> PauseWritesAsync(CancellationToken ct = default);
}
