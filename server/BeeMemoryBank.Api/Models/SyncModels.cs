namespace BeeMemoryBank.Api.Models;

/// <summary>Returned in response to /api/sync/challenge.</summary>
public record SyncChallengeResponse(string Challenge, Guid ServerNodeId);

/// <summary>Node authentication request.</summary>
public record SyncAuthRequest(Guid NodeId, string ChallengeB64, string SignatureB64);

/// <summary>Response after successful authentication.</summary>
public record SyncAuthResponse(string Token);

/// <summary>Identity of this node for remote nodes.</summary>
public record SyncIdentityResponse(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion);

/// <summary>Result of applying events.</summary>
/// <param name="Applied">Count of events successfully persisted via ApplyAsync.</param>
/// <param name="Skipped">Count of events rejected (signature, schema, replay shield, etc.).</param>
/// <param name="LastAppliedSequence">
/// Sequence_num of the highest event in this batch that was successfully applied.
/// The pusher MUST use this — not batch[^1].SequenceNum — when advancing its push cursor.
/// Otherwise events that the remote skipped get permanently lost: the cursor steps over
/// them and the pusher will never re-send. null means no events applied (all-skipped or
/// empty batch); pusher should leave its cursor unchanged. (Brainstorm bug #3.)
/// </param>
public record SyncApplyResult(int Applied, int Skipped, long? LastAppliedSequence = null, int Dropped = 0);

// ─── Blob transport (protocol 2) ───────────────────────────────────────────────
// Wire shapes for /api/sync/blobs/*. The client side (BlobTransport in BeeMemoryBank.Sync) keeps
// its own private copies of these — the Sync library does not reference Api — so a change here
// must be mirrored there.

/// <summary>Request body for /api/sync/blobs/check and /api/sync/blobs/get.</summary>
public record SyncBlobHashList(List<string> Hashes);

/// <summary>Response of /api/sync/blobs/check: the requested hashes this node does NOT hold.</summary>
public record SyncBlobMissing(List<string> Missing);

/// <summary>One blob on the wire: lowercase-hex SHA-256 and base64 ciphertext.</summary>
public record SyncBlob(string Hash, string Data);

/// <summary>Request body of POST /api/sync/blobs and response of /api/sync/blobs/get.</summary>
public record SyncBlobBatch(List<SyncBlob> Blobs);

/// <summary>
/// Response of POST /api/sync/blobs. Rejected counts items whose base64 was malformed — a blob
/// whose bytes hash to something other than the claimed hash is not rejected, it is stored under
/// its real hash (see BlobRepository.StoreAsync), so it can never shadow the content an event
/// actually asked for.
/// </summary>
public record SyncBlobStoreResult(int Stored, int Rejected);

public record DeliveryNodeStatus(
    Guid NodeId,
    string DisplayName,
    string NodeType,
    long LastPushedSeq,
    long HeadSeq,
    int UnsyncedCount,
    bool IsSynced,
    DateTime? LastContactAt);

public record DeliveryStatusResponse(
    Guid? LocalNodeId,
    bool IsInvisible,
    List<DeliveryNodeStatus> Nodes);
