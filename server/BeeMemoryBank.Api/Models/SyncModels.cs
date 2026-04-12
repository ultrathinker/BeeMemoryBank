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
public record SyncApplyResult(int Applied, int Skipped);

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
