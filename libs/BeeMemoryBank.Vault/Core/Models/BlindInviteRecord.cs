namespace BeeMemoryBank.Core.Models;

/// <summary>The states a row of <c>tbl_blind_invite</c> can be in, as the hub keeps them (the wire has its own, <see cref="BlindInviteStates"/>).</summary>
public static class BlindInviteRowState
{
    public const string Issued = "issued";
    public const string Redeemed = "redeemed";

    /// <summary>The owner confirmed and the enrolment (whitelist_add, the seal) is under way or was interrupted: a reconciler finishes it or undoes it.</summary>
    public const string Confirming = "confirming";
    public const string Confirmed = "confirmed";
    public const string Refused = "refused";
    public const string Expired = "expired";

    public static bool IsFinal(string state) => state is Confirmed or Refused or Expired;
}

/// <summary>What kind of blind node redeemed an invitation (<c>tbl_blind_invite.kind</c>, migration 042).</summary>
public static class BlindInviteKinds
{
    /// <summary>An app copy (a phone or a desktop): the owner's confirmation enrols it and seals its backup key.</summary>
    public const string Device = "device";

    /// <summary>A server blind node (<c>bmb blind join</c>): the owner's confirmation adds and seeds it with the pair code it sent, as pasting that code on the Blind nodes page does.</summary>
    public const string Server = "server";
}

/// <summary>
/// One invitation this node made (<c>tbl_blind_invite</c>, migration 041, kind and pair code 042) and, once a device redeemed it, who that was. Local to the node: never
/// replicated, never in a snapshot or baseline. The secret is not here, only its hash; <see cref="BackupKeyEnc"/> is the device's backup key under the
/// master DEK between the redemption and the owner's decision, and null in every final state.
/// </summary>
public sealed class BlindInviteRecord
{
    public string InviteId { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public string Label { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string Address { get; set; } = "";
    public string Trust { get; set; } = "";
    public string? Pin { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string State { get; set; } = BlindInviteRowState.Issued;
    public DateTime? RedeemedAt { get; set; }
    public string? RedeemerNodeId { get; set; }
    public string? RedeemerKey { get; set; }
    public string? RedeemerName { get; set; }
    public string? RedeemerPlatform { get; set; }
    public string? RedeemerVersion { get; set; }
    public string? RedeemerSeenFrom { get; set; }
    public byte[]? BackupKeyEnc { get; set; }

    /// <summary><see cref="BlindInviteKinds"/>: set by the redemption (an invitation nobody redeemed is a device one by default).</summary>
    public string Kind { get; set; } = BlindInviteKinds.Device;

    /// <summary>A server node's pair code under the master DEK between the redemption and the owner's decision; null for a device and in every final state.</summary>
    public byte[]? PairCodeEnc { get; set; }
    public DateTime? ConfirmDeadline { get; set; }

    /// <summary>When the row became <see cref="BlindInviteRowState.Confirming"/>.</summary>
    public DateTime? ConfirmingAt { get; set; }

    /// <summary>
    /// The id of the <c>whitelist_add</c> event this invitation's enrolment logs, fixed when the row becomes <see cref="BlindInviteRowState.Confirming"/>, before anything
    /// is written outside the row. Whether the event exists in the log says how far the enrolment got; an undo revokes by it.
    /// </summary>
    public string? EnrollEventId { get; set; }

    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}
