using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>Keys of <c>tbl_boot_meta</c> (docs/design/step-b-spec.md Appendix B). Values are text.</summary>
public static class BootMetaKeys
{
    public const string Format = "format";                       // "1"
    public const string NodeId = "node_id";                      // this node's id, "D" format
    public const string DbkGeneration = "dbk_generation";        // generation of the database key in use (spec 3.1)
    public const string SealedAt = "sealed_at";                  // UTC "o" of the migration
    public const string Activated = "activated";                 // "1": Step A applied here (always on a sealed node)
    public const string SeqFloor = "seq_floor";                  // first relay sequence the inbox may use (spec 6.3)
    public const string SealedHead = "sealed_head";              // tbl_event head at the last clean close, or ""
    public const string CleanClose = "clean_close";              // "1" after a clean close, "0" while open
    public const string UnlockMaterialVersion = "unlock_material_version"; // last mirrored value (spec 4.3)
    public const string LastRefusal = "last_refusal";                       // code of the last refused unlock (spec 4.4), or ""
    public const string ServerAutoUnlock = "server_auto_unlock";           // "1" while the server checkbox is on (spec 11.2)
}

public static class BootDoorKinds
{
    public const string Slot = "slot";
    public const string Agent = "agent";
}

/// <summary>
/// One way to obtain the master DEK before unlock (spec 4.1): a copy of a key slot or of an auto-unlock agent's wrapped DEK. Never a
/// user name, an agent name or a description.
/// </summary>
public sealed record BootDoor(
    long DoorId,
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
    string ContentSha256,
    string? DekFingerprint,
    bool Pending);

/// <summary>The database key wrapped under one master DEK (spec 3.3).</summary>
public sealed record BootDbKeyRow(string DekFingerprint, int DbkGeneration, byte[] Wrapped, byte[] Iv, DateTime CreatedAt);

/// <summary>The public part of this node's identity, for the relay routes while closed (spec 6.1).</summary>
public sealed record BootIdentity(Guid NodeId, string DisplayName, byte[] Ed25519PublicKey);

/// <summary>An event received while the database was closed, as stored and served (spec 6.2).</summary>
public sealed record RelayEventRow(long RelaySeq, SyncEvent Event, Guid? OriginPeer, long? OriginSeq, DateTime ReceivedAt);

public sealed record RelayInboxSummary(long Events, long EventBytes, long Blobs, long BlobBytes, DateTime? OldestReceivedAt, long MaxRelaySeq);

/// <summary>The boot store file itself (track 2): created, migrated, its meta rows.</summary>
public interface IBootStore
{
    string FilePath { get; }

    bool Exists { get; }

    /// <summary>Creates the file if needed (application id, WAL) and runs the boot migrations (own ledger, spec 2.3).</summary>
    Task EnsureCreatedAsync(CancellationToken ct = default);

    Task<string?> GetMetaAsync(string key, CancellationToken ct = default);

    Task SetMetaAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>The doors (track 2). Read by the unlock (track 3), written only through the mirror (spec 4.3).</summary>
public interface IBootDoorStore
{
    Task<IReadOnlyList<BootDoor>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BootDoor>> FindAgentAsync(string keyHash, CancellationToken ct = default);

    /// <summary>Records which master DEK a door yielded (spec 3.4); called by the unlock that used it.</summary>
    Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default);
}

/// <summary>The DBK wrappers (track 2).</summary>
public interface IBootDbKeyStore
{
    Task<BootDbKeyRow?> FindAsync(string dekFingerprint, int dbkGeneration, CancellationToken ct = default);

    Task<IReadOnlyList<BootDbKeyRow>> ListAsync(CancellationToken ct = default);
}

/// <summary>The identity and whitelist mirrors the relay authenticates with while closed (track 2, spec 2.2, 6.4).</summary>
public interface IBootMirrorStore
{
    Task<BootIdentity?> GetIdentityAsync(CancellationToken ct = default);

    /// <summary>An active (status 'A') mirror row, or null.</summary>
    Task<(byte[] PublicKey, bool IsSuperadmin)?> GetActivePeerAsync(Guid nodeId, CancellationToken ct = default);

    /// <summary>Marks a mirror row 'S' (suspended until the next rewrite) after a verified revoke or update received while closed.</summary>
    Task SuspendAsync(Guid nodeId, CancellationToken ct = default);
}

/// <summary>Events, blobs and position reports received while the database is closed (track 2, spec 6).</summary>
public interface IRelayInbox
{
    /// <summary>Stores the event unless its id is already stored; returns its relay sequence (the stored one when it was there).</summary>
    Task<long> AppendAsync(SyncEvent evt, Guid? originPeer, long? originSeq, CancellationToken ct = default);

    Task<IReadOnlyList<RelayEventRow>> ReadAfterAsync(long afterRelaySeq, int limit, CancellationToken ct = default);

    Task DeleteAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default);

    Task<RelayInboxSummary> GetSummaryAsync(CancellationToken ct = default);

    Task StoreBlobAsync(string hash, byte[] ciphertext, CancellationToken ct = default);

    Task<byte[]?> GetBlobAsync(string hash, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListBlobHashesAsync(CancellationToken ct = default);

    Task DeleteBlobsAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default);

    Task RecordReportAsync(Guid peer, long sequence, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid Peer, long Sequence)>> ListReportsAsync(CancellationToken ct = default);

    Task ClearReportsAsync(CancellationToken ct = default);
}
