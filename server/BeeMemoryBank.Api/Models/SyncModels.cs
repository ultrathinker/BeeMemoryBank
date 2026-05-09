namespace BeeMemoryBank.Api.Models;

/// <summary>Returned in response to /api/sync/challenge.</summary>
public record SyncChallengeResponse(string Challenge, Guid ServerNodeId);

/// <summary>Node authentication request.</summary>
public record SyncAuthRequest(Guid NodeId, string ChallengeB64, string SignatureB64);

/// <summary>Response after successful authentication.</summary>
public record SyncAuthResponse(string Token);

/// <summary>Identity of this node for remote nodes.</summary>
public record SyncIdentityResponse(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

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

public record DeliveryNodeStatus(
    Guid NodeId,
    string DisplayName,
    string NodeType,
    long LastPushedSeq,
    long TotalLocalEvents,
    bool IsSynced,
    DateTime? LastContactAt);

public record DeliveryStatusResponse(
    Guid? LocalNodeId,
    bool IsInvisible,
    List<DeliveryNodeStatus> Nodes);
