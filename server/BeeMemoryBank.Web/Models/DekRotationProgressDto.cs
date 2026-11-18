namespace BeeMemoryBank.Web.Models;

public record DekRotationProgressDto(
    Guid? EventId,
    string CurrentStep,
    int PercentageComplete,
    string? StatusMessage,
    string? ErrorMessage
);

/// <summary>One old-format (v1) door the bridge from an old master key is kept for.</summary>
public record DekBridgeDoorDto(int SlotId, string Kind, string? OwnerName);

/// <summary>The bridge from an old master key on this node (2.12): present while a v1 door exists.</summary>
/// <summary>GET /api/node/identity (release 2.12, plan WS-D).</summary>
public record NodeIdentityKeyDto(Guid NodeId, string Fingerprint, long Counter, bool CanRekeyHere, List<string> PeersHoldingBack,
    List<NodeKeyHistoryItemDto> History, List<NodeKeyConflictItemDto> Conflicts, List<RetiredKeyAttentionItemDto>? RetiredKeyAttention = null,
    SignedAsThisNodeDto? SignedAsThisNode = null);

/// <summary>Events signed with this node's key that it did not write.</summary>
public record SignedAsThisNodeDto(long Count, string SetAt);

public record RetiredKeyAttentionItemDto(Guid NodeId, string? DisplayName, long Count, string SetAt);

public record NodeKeyHistoryItemDto(long Counter, string RetiredFingerprint, string NewFingerprint, Guid RotationEventId);

public record NodeKeyConflictItemDto(Guid NodeId, string? DisplayName, long Counter, string FirstFingerprint, string SecondFingerprint, string DetectedAt,
    Guid FirstRotationEventId, Guid SecondRotationEventId);

public record DekBridgeDto(bool BridgePresent, int V1DoorCount, List<DekBridgeDoorDto> Doors, string? SinceRotation, string? SinceAppliedAt);
