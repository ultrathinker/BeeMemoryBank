namespace BeeMemoryBank.Api.Models;

/// <summary>Returned in response to /api/sync/challenge.</summary>
public record SyncChallengeResponse(string Challenge, Guid ServerNodeId);

/// <summary>Node authentication request.</summary>
/// <param name="ProtocolVersion">
/// The protocol the caller declares (ISyncProtocolPolicy.Declared: 3, or 4 once it applied the metadata activation). Absent
/// (null) from builds before protocol 3. Recorded on the caller's whitelist row (WhitelistEntry.LastProtocolVersion); one this
/// node does not accept (below SyncProtocolVersion.MinPeer, or after activation below 4 without the capability), or absent,
/// gets no token (426).
/// </param>
/// <param name="Capabilities">What the caller's build advertises (docs/design/step-a-spec.md 5.1); absent from 2.5 builds.</param>
/// <param name="AppVersion">The caller's application version; absent from 2.5 builds.</param>
public record SyncAuthRequest(Guid NodeId, string ChallengeB64, string SignatureB64, int? ProtocolVersion = null,
    IReadOnlyList<string>? Capabilities = null, string? AppVersion = null);

/// <summary>Response after successful authentication.</summary>
public record SyncAuthResponse(string Token);

/// <summary>Identity of this node for remote nodes.</summary>
/// <param name="ProtocolVersion">What this node declares: 3, or 4 once it applied the metadata activation.</param>
/// <param name="Capabilities">What this build advertises (docs/design/step-a-spec.md 5.1); older nodes ignore it.</param>
public record SyncIdentityResponse(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion,
    IReadOnlyList<string>? Capabilities = null, string? AppVersion = null);

/// <summary>Result of applying events.</summary>
/// <param name="Applied">Count of events successfully persisted via ApplyAsync.</param>
/// <param name="Skipped">Count of events rejected (signature, schema, replay shield, etc.).</param>
/// <param name="LastAppliedSequence">
/// Sequence_num of the last event in the contiguous batch prefix that was successfully applied or
/// deliberately dropped. It never advances past a skipped event, even if later events in the same
/// request are accepted.
/// The pusher MUST use this — not batch[^1].SequenceNum — when advancing its push cursor.
/// Otherwise events that the remote skipped get permanently lost: the cursor steps over
/// them and the pusher will never re-send. null means the accepted prefix is empty (all-skipped,
/// a first-item skip, or an empty batch); pusher should leave its cursor unchanged.
/// </param>
/// <param name="Held">Step A (docs/design/step-a-spec.md 6.2), informative: how many of <paramref name="Applied"/> were stored
/// held (a locked node, a key not here yet). They count as applied, so a pusher of any version advances over them.</param>
public record SyncApplyResult(int Applied, int Skipped, long? LastAppliedSequence = null, int Dropped = 0, int Held = 0);

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
/// Response of POST /api/sync/blobs. Rejected counts items whose base64 was malformed, that were
/// empty, or whose bytes do not hash to the claimed hash — those are not stored at all.
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
