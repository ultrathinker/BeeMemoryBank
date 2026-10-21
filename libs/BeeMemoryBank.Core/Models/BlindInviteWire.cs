using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Models;

/// <summary>States a redemption can be in, as the hub tells the device (<see cref="BlindInviteStatusAnswer"/>).</summary>
public static class BlindInviteStates
{
    /// <summary>Redeemed; the owner has not decided yet.</summary>
    public const string Awaiting = "awaiting";
    public const string Confirmed = "confirmed";
    public const string Refused = "refused";
    public const string Expired = "expired";

    public static bool IsKnown(string? state) => state is Awaiting or Confirmed or Refused or Expired;
}

/// <summary>The <c>error</c> words of the invitation routes: one for everything that is not a valid invitation, so a probe learns nothing from the wording.</summary>
public static class BlindInviteErrors
{
    /// <summary>410: unknown, expired, already used by another device, withdrawn — one answer for all of them.</summary>
    public const string NotValid = "invite_not_valid";

    /// <summary>409: this node id is already on the hub's whitelist under another key.</summary>
    public const string DeviceKnownUnderOtherKey = "device_known_under_other_key";

    /// <summary>503: the hub's vault is locked; the device tries again until the invitation expires.</summary>
    public const string Locked = "locked";
}

/// <summary>
/// The proofs a device signs with the key it registers, and their checks on the hub. The message is a domain label and the fields, separated by line breaks, as
/// text: ids and keys base64url, node ids in the lower-case 36-character <c>D</c> form, numbers as invariant decimals.
/// <list type="bullet">
/// <item>redeem: <c>bmb-invite-redeem-v1\n{inviteId}\n{hubNodeId}\n{nodeId}\n{publicKey}\n{SHA-256(backupKey)}</c> — the device holds the key it registers (nobody can enrol another
/// device's id or key), naming the hub stops a redemption from being replayed at another node, and the hash ties the backup key to it without signing the key itself;</item>
/// <item>status: <c>bmb-invite-status-v1\n{inviteId}\n{nodeId}\n{at}</c> with <c>at</c> in unix seconds, accepted within <see cref="StatusWindow"/> of the hub's clock.</item>
/// </list>
/// </summary>
public static class BlindInviteProofs
{
    public static readonly TimeSpan StatusWindow = TimeSpan.FromMinutes(10);

    public static byte[] RedeemMessage(byte[] inviteId, Guid hubNodeId, Guid nodeId, byte[] publicKey, byte[] backupKey) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "bmb-invite-redeem-v1", Base64Url.EncodeToString(inviteId), hubNodeId.ToString("D"), nodeId.ToString("D"),
            Base64Url.EncodeToString(publicKey), Base64Url.EncodeToString(SHA256.HashData(backupKey))));

    /// <summary>
    /// The redeem proof of a SERVER blind node (<c>bmb-invite-redeem-server-v1</c>, its own label): the same fields, but the last one is the SHA-256 of the pair code text it carries instead of the
    /// hash of a backup key (a server node has none). Another label, so that a signature made for one kind of redemption can never be read as the other's.
    /// </summary>
    public static byte[] ServerRedeemMessage(byte[] inviteId, Guid hubNodeId, Guid nodeId, byte[] publicKey, string pairCode) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "bmb-invite-redeem-server-v1", Base64Url.EncodeToString(inviteId), hubNodeId.ToString("D"), nodeId.ToString("D"),
            Base64Url.EncodeToString(publicKey), Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(pairCode)))));

    public static byte[] StatusMessage(byte[] inviteId, Guid nodeId, long at) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "bmb-invite-status-v1", Base64Url.EncodeToString(inviteId), nodeId.ToString("D"), at.ToString(CultureInfo.InvariantCulture)));

    /// <summary>True if <paramref name="request"/> carries a valid proof for the hub <paramref name="hubNodeId"/> (by the kind of redemption it is). Never throws.</summary>
    public static bool RedeemProofIsValid(BlindInviteRedeemRequest request, Guid hubNodeId) =>
        request switch
        {
            { PairCode: { } pairCode, BackupKey: null } =>
                Verify(request.PublicKey, ServerRedeemMessage(request.InviteId, hubNodeId, request.NodeId, request.PublicKey, pairCode), request.Proof),
            { PairCode: null, BackupKey: { } backupKey } =>
                Verify(request.PublicKey, RedeemMessage(request.InviteId, hubNodeId, request.NodeId, request.PublicKey, backupKey), request.Proof),
            _ => false,
        };

    /// <summary>
    /// True if the status request was signed by <paramref name="publicKey"/> (the key the redemption registered) and <c>at</c> is within <see cref="StatusWindow"/>
    /// of <paramref name="now"/>, either way. Never throws.
    /// </summary>
    public static bool StatusProofIsValid(BlindInviteStatusRequest request, byte[] publicKey, DateTimeOffset now) =>
        Math.Abs(now.ToUnixTimeSeconds() - request.At) <= (long)StatusWindow.TotalSeconds
        && Verify(publicKey, StatusMessage(request.InviteId, request.NodeId, request.At), request.Signature);

    private static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != CryptoConstants.Ed25519PublicKeySize || signature.Length != BlindInvitation.SignatureSize) return false;
        try { return Ed25519Signer.Verify(publicKey, message, signature); }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// <c>POST /api/sync/invite/redeem</c>, device → hub. Strict JSON, at most <see cref="MaxBodyBytes"/>: these members, each once, of the right type, with the right lengths; anything
/// else is not a request. <c>secret</c>, <c>backupKey</c> and <c>pairCode</c> are bearer material and never written to a log.
///
/// <para>Two kinds of device redeem. An APP copy (a phone, a desktop) sends the ten members it always sent, <c>backupKey</c> among them. A SERVER blind node (release 2.7,
/// <c>bmb blind join</c>) sends <c>pairCode</c> - its own pair code, which lets the owner's confirmation add and seed it the way pasting the code on the Blind nodes page does -
/// INSTEAD of <c>backupKey</c> (it has none), and signs a proof of its own label (<see cref="BlindInviteProofs.ServerRedeemMessage"/>). Exactly one of the two is present.</para>
///
/// <para><b>How this body evolves.</b> A member is added only as an optional one, and the invitation text stays <c>v=1</c> and the sync protocol is not bumped: an invitation is a
/// bearer capability and its text carries no feature list. A hub that does not know a member refuses the WHOLE body (400 <c>bad_request</c>, nothing read, nothing written): that
/// is how a full node older than the feature "refuses cleanly", and why a sender of the new member reads a 400 as "this full node has to be upgraded first". A change in what a
/// member MEANS is not an addition: it needs a new invitation version.</para>
/// </summary>
public sealed record BlindInviteRedeemRequest(
    byte[] InviteId, byte[] Secret, Guid NodeId, byte[] PublicKey, string DisplayName, string Platform, string AppVersion, int Protocol,
    byte[]? BackupKey, byte[] Proof, string? PairCode = null)
{
    public const int MaxBodyBytes = 8 * 1024;
    public const int MaxPlatformLength = 16;
    public const int MaxVersionLength = 32;
    public const int MaxProtocol = 10_000;
    public const int BackupKeySize = 32;

    /// <summary>What a pair code starts with (<c>BlindPairCode.Prefix</c>; the type is in the Sync library, which Core cannot see).</summary>
    public const string PairCodePrefix = "BMBBLIND1.";

    /// <summary>A real pair code is about 450 characters (a 255-character address included); a longer text is not one.</summary>
    public const int MaxPairCodeLength = 1500;

    private static readonly string[] Strings = ["inviteId", "secret", "nodeId", "publicKey", "displayName", "platform", "appVersion", "proof"];
    private static readonly string[] OptionalStrings = ["backupKey", "pairCode"];

    /// <summary>True for a server blind node's redemption (it carries its pair code and no backup key).</summary>
    public bool IsServerJoin => PairCode is not null;

    /// <summary>The framework would print every member of a record, and three of them are bearer material.</summary>
    public override string ToString() => $"BlindInviteRedeemRequest {{ NodeId = {NodeId}, Kind = {(IsServerJoin ? "server" : "device")}, secrets = *** }}";

    public byte[] ToJson() => BlindInviteJson.Write(w =>
    {
        w.WriteString("inviteId", Base64Url.EncodeToString(InviteId));
        w.WriteString("secret", Base64Url.EncodeToString(Secret));
        w.WriteString("nodeId", NodeId.ToString("D"));
        w.WriteString("publicKey", Base64Url.EncodeToString(PublicKey));
        w.WriteString("displayName", DisplayName);
        w.WriteString("platform", Platform);
        w.WriteString("appVersion", AppVersion);
        w.WriteNumber("protocol", Protocol);
        if (PairCode is not null) w.WriteString("pairCode", PairCode);
        else w.WriteString("backupKey", Base64Url.EncodeToString(BackupKey ?? throw new InvalidOperationException("A redemption carries a backup key or a pair code.")));
        w.WriteString("proof", Base64Url.EncodeToString(Proof));
    });

    /// <summary>Parses a request body. The node id must carry the blind mark; whether the proof verifies is <see cref="BlindInviteProofs.RedeemProofIsValid"/>'s question. Never throws.</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out BlindInviteRedeemRequest? request)
    {
        request = null;
        if (!BlindInviteJson.TryRead(body, Strings, ["protocol"], OptionalStrings, out var text, out var number)) return false;

        if (CodeText.Base64UrlOfLength(text["inviteId"], BlindInvitation.InviteIdSize) is not { } inviteId) return false;
        if (CodeText.Base64UrlOfLength(text["secret"], BlindInvitation.SecretSize) is not { } secret) return false;
        if (!Guid.TryParseExact(text["nodeId"], "D", out var nodeId) || !BlindNodeId.IsBlind(nodeId)) return false;
        if (CodeText.Base64UrlOfLength(text["publicKey"], CryptoConstants.Ed25519PublicKeySize) is not { } key) return false;
        if (!BlindInviteText.IsPlain(text["displayName"], BlindPhoneCode.MaxDisplayNameLength)) return false;
        if (!IsToken(text["platform"], MaxPlatformLength) || !IsToken(text["appVersion"], MaxVersionLength)) return false;
        if (number["protocol"] is < 0 or > MaxProtocol) return false;
        // Exactly one of the two: a redemption that carries both says two things about what the device is.
        var hasBackupKey = text.TryGetValue("backupKey", out var backupKeyText);
        var hasPairCode = text.TryGetValue("pairCode", out var pairCode);
        if (hasBackupKey == hasPairCode) return false;
        byte[]? backupKey = null;
        if (hasBackupKey && (backupKey = CodeText.Base64UrlOfLength(backupKeyText, BackupKeySize)) is null) return false;
        if (hasPairCode && !LooksLikeAPairCode(pairCode)) return false;
        if (CodeText.Base64UrlOfLength(text["proof"], BlindInvitation.SignatureSize) is not { } proof) return false;

        request = new BlindInviteRedeemRequest(inviteId, secret, nodeId, key, text["displayName"], text["platform"], text["appVersion"], (int)number["protocol"], backupKey, proof,
            hasPairCode ? pairCode : null);
        return true;
    }

    /// <summary>The shape only (the prefix and URL-safe base64 after it, not too long): what the pair code SAYS is read by the hub's pairing code (<c>BlindPairCode.Parse</c>).</summary>
    private static bool LooksLikeAPairCode(string? text) =>
        text is { Length: > 0 and <= MaxPairCodeLength }
        && text.StartsWith(PairCodePrefix, StringComparison.Ordinal) && text.Length > PairCodePrefix.Length
        && text.AsSpan(PairCodePrefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>Empty, or letters, digits and <c>. + - _</c> only: a platform or version a peer states is never shown as it came, but it is never stored as garbage either.</summary>
    internal static bool IsToken(string value, int maxLength) =>
        value.Length <= maxLength && value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '+' or '-' or '_');

    /// <summary>
    /// A platform or a version as the hub takes it (letters, digits and <c>. + - _</c> only, at most <paramref name="max"/>): what does not fit is left out, never sent as it was.
    /// For a sender that states what it runs on (<c>bmb blind join</c> does).
    /// </summary>
    public static string TokenOf(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = new string(value.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '+' or '-' or '_').ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }
}

/// <summary>The hub's 202 to a redemption: the device shows the match code and waits.</summary>
/// <param name="ConfirmByUnix">Unix seconds: when the owner's chance to confirm ends.</param>
public sealed record BlindInviteRedeemAnswer(string State, string MatchCode, string SourceName, long ConfirmByUnix)
{
    public byte[] ToJson() => BlindInviteJson.Write(w =>
    {
        w.WriteString("state", State);
        w.WriteString("matchCode", MatchCode);
        w.WriteString("sourceName", SourceName);
        w.WriteNumber("confirmBy", ConfirmByUnix);
    });

    public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out BlindInviteRedeemAnswer? answer)
    {
        answer = null;
        if (!BlindInviteJson.TryRead(body, ["state", "matchCode", "sourceName"], ["confirmBy"], out var text, out var number)) return false;
        if (text["state"] != BlindInviteStates.Awaiting) return false;
        if (BlindInviteMatchCode.Normalize(text["matchCode"]) is not { } code) return false;
        if (!BlindInviteText.IsPlain(text["sourceName"], BlindInvitation.MaxSourceNameLength)) return false;
        if (number["confirmBy"] is < 1 or > 253402300799) return false;
        answer = new BlindInviteRedeemAnswer(BlindInviteStates.Awaiting, code, text["sourceName"], number["confirmBy"]);
        return true;
    }
}

/// <summary><c>POST /api/sync/invite/status</c>, device → hub: signed with the key the redemption registered (see <see cref="BlindInviteProofs"/>).</summary>
public sealed record BlindInviteStatusRequest(byte[] InviteId, Guid NodeId, long At, byte[] Signature)
{
    public const int MaxBodyBytes = BlindInviteRedeemRequest.MaxBodyBytes;

    public byte[] ToJson() => BlindInviteJson.Write(w =>
    {
        w.WriteString("inviteId", Base64Url.EncodeToString(InviteId));
        w.WriteString("nodeId", NodeId.ToString("D"));
        w.WriteNumber("at", At);
        w.WriteString("signature", Base64Url.EncodeToString(Signature));
    });

    public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out BlindInviteStatusRequest? request)
    {
        request = null;
        if (!BlindInviteJson.TryRead(body, ["inviteId", "nodeId", "signature"], ["at"], out var text, out var number)) return false;
        if (CodeText.Base64UrlOfLength(text["inviteId"], BlindInvitation.InviteIdSize) is not { } inviteId) return false;
        if (!Guid.TryParseExact(text["nodeId"], "D", out var nodeId) || !BlindNodeId.IsBlind(nodeId)) return false;
        if (CodeText.Base64UrlOfLength(text["signature"], BlindInvitation.SignatureSize) is not { } signature) return false;
        if (number["at"] is < 1 or > 253402300799) return false;
        request = new BlindInviteStatusRequest(inviteId, nodeId, number["at"], signature);
        return true;
    }
}

/// <summary>The hub's answer to a status request.</summary>
public sealed record BlindInviteStatusAnswer(string State)
{
    public byte[] ToJson() => BlindInviteJson.Write(w => w.WriteString("state", State));

    public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out BlindInviteStatusAnswer? answer)
    {
        answer = null;
        if (!BlindInviteJson.TryRead(body, ["state"], [], out var text, out _) || !BlindInviteStates.IsKnown(text["state"])) return false;
        answer = new BlindInviteStatusAnswer(text["state"]);
        return true;
    }
}

/// <summary>
/// Strict reading of the small JSON objects of the invitation routes: one object, exactly the named members (strings and integers), each once, nothing nested, no
/// comments, no trailing commas, nothing after the object, at most <see cref="BlindInviteRedeemRequest.MaxBodyBytes"/>. A reader that quietly ignored an unknown member
/// or took the second of two equal ones would let two parties disagree on what was signed.
/// </summary>
internal static class BlindInviteJson
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 2 };

    public static byte[] Write(Action<Utf8JsonWriter> members)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static bool TryRead(ReadOnlySpan<byte> body, string[] stringMembers, string[] numberMembers,
        [NotNullWhen(true)] out Dictionary<string, string>? strings, [NotNullWhen(true)] out Dictionary<string, long>? numbers) =>
        TryRead(body, stringMembers, numberMembers, [], out strings, out numbers);

    /// <summary>As above, and the <paramref name="optionalStringMembers"/> may be present (once) or absent; any other member is still refused.</summary>
    public static bool TryRead(ReadOnlySpan<byte> body, string[] stringMembers, string[] numberMembers, string[] optionalStringMembers,
        [NotNullWhen(true)] out Dictionary<string, string>? strings, [NotNullWhen(true)] out Dictionary<string, long>? numbers)
    {
        strings = null;
        numbers = null;
        if (body.IsEmpty || body.Length > BlindInviteRedeemRequest.MaxBodyBytes) return false;
        try
        {
            var reader = new Utf8JsonReader(body, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            var text = new Dictionary<string, string>(StringComparer.Ordinal);
            var number = new Dictionary<string, long>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                if (!reader.Read()) return false;
                if ((stringMembers.Contains(name, StringComparer.Ordinal) || optionalStringMembers.Contains(name, StringComparer.Ordinal)) && reader.TokenType == JsonTokenType.String)
                {
                    if (text.ContainsKey(name) || reader.GetString() is not { } value) return false;
                    text[name] = value;
                }
                else if (numberMembers.Contains(name, StringComparer.Ordinal) && reader.TokenType == JsonTokenType.Number)
                {
                    if (number.ContainsKey(name) || !reader.TryGetInt64(out var value)) return false;
                    number[name] = value;
                }
                else
                {
                    return false;
                }
            }
            // The loop ended on the closing brace (a JsonException comes from anything else), and nothing may follow it.
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            // Every required member is there (a member can only be present once, so the counts say it); the optional ones are whatever came.
            if (stringMembers.Any(m => !text.ContainsKey(m)) || number.Count != numberMembers.Length) return false;

            strings = text;
            numbers = number;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
