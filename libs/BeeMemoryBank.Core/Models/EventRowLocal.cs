namespace BeeMemoryBank.Core.Models;

/// <summary>The values of tbl_event.apply_state (migration 038, docs/design/step-a-spec.md 6.1).</summary>
public static class EventApplyStates
{
    public const int Applied = 0;
    /// <summary>Stored, not applied yet: the vault is locked, the key is missing, or an earlier row is held.</summary>
    public const int Held = 1;
    /// <summary>A blind host stored a sealed event it can never open; storing and relaying it is all it does.</summary>
    public const int Relayed = 2;
    /// <summary>Stored and relayed while held, then found not to apply (it does not open, or a handler refused it).</summary>
    public const int QuarantinedAfterReceipt = 3;
}

/// <summary>
/// The local columns of a row of tbl_event (spec 6.1): how this node holds the event, the opened view's type and article
/// id on a full node, and who delivered it. Never served: the wire queries read the original columns only.
/// </summary>
public sealed record EventRowLocal(
    int ApplyState = EventApplyStates.Applied,
    string? HoldReason = null,
    bool? OriginSuperadmin = null,
    string? LocalType = null,
    Guid? LocalArticleId = null,
    Guid? OriginPeer = null,
    long? OriginSeq = null);

/// <summary>A held row as the drain reads it: the original event and its receipt (spec 6.5).</summary>
public sealed record HeldEventRow(
    SyncEvent Event, string? HoldReason, bool OriginSuperadmin, DateTime? HeldAt, Guid? OriginPeer, long? OriginSeq);
