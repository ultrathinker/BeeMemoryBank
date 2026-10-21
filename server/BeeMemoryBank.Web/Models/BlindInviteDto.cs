namespace BeeMemoryBank.Web.Models;

/// <summary>An invitation as GET /api/blind-nodes/invites lists it (see BlindInviteService.ListAsync): never the secret, never a key.</summary>
/// <param name="State"><c>issued</c> (nobody redeemed it), <c>redeemed</c> (waiting for the owner), <c>confirming</c> (the owner said yes and the device is being added),
/// <c>confirmed</c>, <c>refused</c> or <c>expired</c>.</param>
/// <param name="MatchCode">The code the friend reads out, once a device redeemed (<c>K7F-2QX</c>).</param>
/// <param name="Kind"><c>device</c> (an app copy) or <c>server</c> (a blind node that joined with <c>bmb blind join</c>); a full node older than this field leaves it out, and it is a device.</param>
public record BlindInviteDto(
    string InviteId, string State, string Label, string SourceName, string Address, string Trust, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt,
    string? DeviceName, string? Platform, string? AppVersion, string? SeenFrom, DateTime? RedeemedAt, string? MatchCode, DateTime? ConfirmDeadline,
    DateTime? DecidedAt, string? DecidedBy, string? DeviceId, string Kind = "device");

/// <summary>What the Api made (POST /api/blind-nodes/invites): the one-time string, shown once. Its <c>ToString()</c> leaves the string out: a generated one would print it.</summary>
public record BlindInviteIssuedDto(string InviteId, string Text, DateTime ExpiresAt, string Address, string Trust, string? Pin, string? Notice)
{
    public override string ToString() => $"BlindInviteIssuedDto {{ InviteId = {InviteId}, Text = ***, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>What the invitation form starts from (GET /api/blind-nodes/invites/defaults).</summary>
public record BlindInviteDefaultsDto(string? Address, string SourceName, int[] Lifetimes, int DefaultLifetime);
