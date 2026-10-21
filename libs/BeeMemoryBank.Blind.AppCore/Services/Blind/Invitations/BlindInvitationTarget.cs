using System.Buffers.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Where an invitation sends the device and what it must find there: the hub's origin, NodeId, identity key and how to trust its TLS endpoint, and the name of the invitation. Everything
/// of <see cref="BlindInvitation"/> EXCEPT the secret, which is held in memory by the flow that redeems and is written nowhere: this record is what may be stored (in the account's
/// state) and shown, and what the account needs after the redemption (to ask where the owner's decision stands, and to build its call code). It keeps the hub's signature and the
/// secret's SHA-256 (what the hub stores of it, and what the signature covers), so that what is read back from the state store can be verified again: the state store is untrusted input.
/// </summary>
public sealed record BlindInvitationTarget(
    string Address, Guid HubNodeId, string Trust, string SpkiPin, byte[] HubPublicKey, byte[] InviteId, long ExpiresAtUnix, string SourceName,
    byte[]? SecretHash = null, byte[]? Signature = null)
{
    public static BlindInvitationTarget From(BlindInvitation invitation) => new(
        invitation.Address, invitation.HubNodeId, invitation.Trust, invitation.SpkiPin, [.. invitation.HubPublicKey], [.. invitation.InviteId],
        invitation.ExpiresAtUnix, invitation.SourceName, invitation.SecretHash(), invitation.Signature is null ? null : [.. invitation.Signature]);

    public string InviteIdText => Base64Url.EncodeToString(InviteId);

    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);

    /// <summary>The host of the hub ("mem.example.org"), for the sentences a person reads; empty when the address cannot be read (it was checked when the invitation was parsed).</summary>
    public string Host => Uri.TryCreate(Address, UriKind.Absolute, out var uri) ? uri.Host : "";

    /// <summary>The code both screens show for the redemption of this device (see <see cref="BlindInviteMatchCode"/>).</summary>
    public string MatchCodeFor(Guid nodeId, byte[] publicKey) => BlindInviteMatchCode.Compute(InviteId, nodeId, publicKey);

    /// <summary>
    /// A call code for this hub under <paramref name="secret"/>, by the trust the invitation names. The flow uses it twice: to pick the HTTP client the trust selects
    /// (<see cref="BlindHttpClientProvider.GetClient(BlindCallCode)"/>, with a secret nobody holds), and, when the owner confirmed, as the account's own call code with a fresh local
    /// secret that is spent at once, so that every path after it (the pinned client, the pull client, the first load, backups) is the one the old pairing already uses.
    /// </summary>
    public BlindCallCode CallCode(byte[] secret) => Trust == BlindTrust.PublicCa
        ? BlindCallCode.CreatePublicCa(Address, HubNodeId, HubPublicKey, secret)
        : BlindCallCode.Create(Address, HubNodeId, SpkiPin, HubPublicKey, secret);
}

/// <summary>
/// What an account that was added from an invitation keeps about it, in its own state store (nothing secret: no invitation text, no secret, no key of the device) until the owner decides:
/// the hub and the invitation (<see cref="BlindInvitationTarget"/>) in ONE value, written whole, so that a crash leaves all of it or none; and, once the hub has answered the
/// redemption, when the owner's time to confirm runs out. Gone (<see cref="Clear"/>) when the account is active, and with the account.
/// </summary>
public sealed class BlindInvitationState(IBlindPhoneStore store)
{
    private const string Key = "bmb.blind.invite";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // V 2: the hub's signature (G) and the secret's hash (H) are kept, so that a value read back is verified again; a value of another version is not read.
    private sealed record Stored(int V, string A, string N, string T, string S, string K, string I, long E, string D, long? C, string? G, string? H);

    /// <summary>True when something is kept (readable or not): a value that is there and gives no <see cref="Target"/> is damaged or was edited, which is not the same as nothing kept.</summary>
    public bool Exists => !string.IsNullOrEmpty(store.Get(Key));

    /// <summary>The invitation's target, or null when none is kept or what is kept cannot be read (a damaged value is never half used).</summary>
    public BlindInvitationTarget? Target => Read() is { } s ? ToTarget(s) : null;

    /// <summary>Unix seconds: when the owner's time to confirm ends, as the hub said when it accepted the redemption; null before that.</summary>
    public long? ConfirmByUnix => Read()?.C;

    public void Save(BlindInvitationTarget target, long? confirmByUnix = null) => Write(new Stored(
        2, target.Address, target.HubNodeId.ToString("D"), target.Trust, target.SpkiPin, Base64Url.EncodeToString(target.HubPublicKey),
        Base64Url.EncodeToString(target.InviteId), target.ExpiresAtUnix, target.SourceName, confirmByUnix,
        target.Signature is null ? null : Base64Url.EncodeToString(target.Signature), target.SecretHash is null ? null : Base64Url.EncodeToString(target.SecretHash)));

    /// <summary>Records the hub's answer to the redemption (its confirm-by time) next to the target. False when no target is kept.</summary>
    public bool SetConfirmBy(long confirmByUnix)
    {
        if (Read() is not { } s) return false;
        Write(s with { C = confirmByUnix });
        return true;
    }

    public void Clear() => store.Set(Key, null);

    private void Write(Stored value) => store.Set(Key, JsonSerializer.Serialize(value, Json));

    private Stored? Read()
    {
        var text = store.Get(Key);
        if (string.IsNullOrEmpty(text)) return null;
        try { return JsonSerializer.Deserialize<Stored>(text, Json) is { V: 2 } s && ToTarget(s) is not null && ConfirmByIsSane(s) ? s : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>The hub's confirm-by time is its redemption time plus 72 hours, and a redemption is made before the invitation ends: never before the expiry, never more than a little over 72 hours after it.</summary>
    private static bool ConfirmByIsSane(Stored s) => s.C is not { } c || ConfirmByIsSane(s.E, c);

    /// <summary>True when <paramref name="confirmByUnix"/> is a deadline the hub could have given for an invitation that ends at <paramref name="expiresAtUnix"/> (the answer of a hub that says otherwise is not taken).</summary>
    public static bool ConfirmByIsSane(long expiresAtUnix, long confirmByUnix) =>
        confirmByUnix >= expiresAtUnix && confirmByUnix <= expiresAtUnix + (long)BlindAccountHost.ConfirmWindow.TotalSeconds + 3600;

    /// <summary>
    /// What was stored, as a target - or null. Read back from a store the app does not own the bytes of, so every rule that holds for an invitation text holds again (the shape of the
    /// address, the trust and its pin, the lengths, the bounds of the name and the expiry) AND the hub's signature verifies against the key the value names: an address or a trust mode that
    /// was changed in the store no longer matches what the hub signed, and the value is refused rather than called.
    /// </summary>
    private static BlindInvitationTarget? ToTarget(Stored s)
    {
        try
        {
            if (!Guid.TryParseExact(s.N, "D", out var hub) || !BlindTrust.IsKnown(s.T)) return null;
            var key = Base64Url.DecodeFromChars(s.K);
            var id = Base64Url.DecodeFromChars(s.I);
            var hash = string.IsNullOrEmpty(s.H) ? null : Base64Url.DecodeFromChars(s.H);
            var signature = string.IsNullOrEmpty(s.G) ? null : Base64Url.DecodeFromChars(s.G);
            if (string.IsNullOrEmpty(s.A) || string.IsNullOrEmpty(s.D)) return null;
            if (BlindInvitation.TargetProblem(s.A, hub, s.T, s.S ?? "", key, id, s.E, s.D, hash, signature) is not null) return null;
            return new BlindInvitationTarget(s.A, hub, s.T, s.S ?? "", key, id, s.E, s.D, hash, signature);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
