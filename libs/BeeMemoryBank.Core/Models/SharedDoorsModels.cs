namespace BeeMemoryBank.Core.Models;

/// <summary>Replicated singleton setting naming the node where user accounts and superadmin doors are managed.</summary>
public sealed record AccountsHomeRecord(
    Guid NodeId,
    long LamportTs,
    Guid? SourceNodeId,
    Guid? EventId,
    DateTime UpdatedAt);

/// <summary>Local state of shared-node doors on this node (singleton row in <c>tbl_shared_doors_state</c>).</summary>
public sealed record SharedDoorsStateRecord(
    string SelfOwnership,
    long SelfOwnershipLamportTs,
    Guid? SelfOwnershipSourceNodeId,
    long LastIssuedVersion,
    string? LastPublishedDigest,
    long LatestReceivedVersion,
    Guid? LatestEventId,
    Guid? LatestAuthorNodeId,
    string? LatestPayloadJson,
    bool PendingApply,
    long AppliedVersion,
    DateTime? AppliedAt,
    Guid? PrimaryPersonId,
    string? LastError,
    bool AccountsHomeChanged = false);

/// <summary>An active non-blind shared peer missing the <c>shared-doors-v1</c> capability.</summary>
public sealed record SharedDoorPeerBlocker(Guid NodeId, string DisplayName);

/// <summary>Snapshot of shared-node accounts home, capability readiness, and local application state.</summary>
public sealed record SharedDoorsStatus(
    Guid? AccountsHomeNodeId,
    string? AccountsHomeName,
    bool IsThisNodeAccountsHome,
    bool IsThisNodeShared,
    bool ShouldOfferAccountsHome,
    IReadOnlyList<SharedDoorPeerBlocker> BlockedSharedNodes,
    SharedDoorsStateRecord LocalState,
    IReadOnlyList<SharedDoorPeerBlocker>? SkippedSharedNodes = null);
