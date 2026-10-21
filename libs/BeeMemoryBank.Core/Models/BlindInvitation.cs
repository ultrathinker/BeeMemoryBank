using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The one-line invitation to a blind copy (release 2.7): the owner of a full node (the "hub" the copy will call, ADR 0007) makes it, the friend pastes or scans it
/// on the device that will hold the copy. It is a one-time capability, NOT an encrypted message: the only thing the two sides share is this string, so a key inside
/// it would protect nothing. What protects it is that it expires, that the first redemption burns it, that it names the hub's identity key and how to trust its TLS
/// endpoint (a pin, or the normal certificate chain), that it carries no secret of the mesh, and that the owner must confirm the redemption against a short match
/// code (<see cref="BlindInviteMatchCode"/>) before the device receives anything.
///
/// <para>Text form: <c>bmb-invite:?v=1&amp;a=&lt;https origin&gt;&amp;n=&lt;hub node id&gt;&amp;t=pin|public-ca&amp;s=&lt;pin, pin mode only&gt;&amp;k=&lt;hub key&gt;&amp;i=&lt;invite id&gt;&amp;x=&lt;secret&gt;&amp;e=&lt;expiry, unix seconds&gt;&amp;d=&lt;source name&gt;&amp;g=&lt;signature&gt;</c>,
/// keys, ids, the pin and the secret base64url. <c>g</c> is REQUIRED: the hub's Ed25519 signature, by the key <c>k</c> of the same string, over every other field
/// (<see cref="SigningInput"/>: a label that carries the version, address, node id, trust, pin, key, invitation id, a hash of the secret, expiry, name). It binds the address and the way to trust it
/// to the hub's key, and it detects a string that was damaged, edited or spliced from two. It does not say who made the string: someone can write a whole invitation of their
/// own with their own hub and key, and a device that takes it joins THEIR mesh and gives them nothing of the owner's invitation.</para>
///
/// <para>Parsing is strict because the string decides where a device sends a secret: a version or trust mode this build does not know, a field it does not know, a
/// repeated key, a wrong length, a control character, an address that is not <c>https://host[:port]</c> or that names a place a node may not call, a pin next to
/// <c>public-ca</c>, or a <c>g</c> that is missing or does not verify, refuse the whole string. Whether it has expired is a separate question (<see cref="IsExpired"/>).</para>
/// </summary>
/// <param name="Address">The hub's <c>https://host[:port]</c>.</param>
/// <param name="HubNodeId">The hub's NodeId: an ordinary (not blind) node.</param>
/// <param name="Trust"><see cref="BlindTrust.Pin"/> or <see cref="BlindTrust.PublicCa"/>.</param>
/// <param name="SpkiPin">The SPKI SHA-256 pin (base64url); empty when <paramref name="Trust"/> is <see cref="BlindTrust.PublicCa"/>.</param>
/// <param name="HubPublicKey">The hub's Ed25519 identity key.</param>
/// <param name="InviteId">16 random bytes: the name of the invitation on the hub.</param>
/// <param name="Secret">32 random bytes: the bearer secret; the hub stores only its SHA-256.</param>
/// <param name="ExpiresAtUnix">Unix seconds, by the hub's clock.</param>
/// <param name="SourceName">What the friend is shown as the owner of the mesh (at most 64 characters).</param>
/// <param name="Signature">The hub's signature over <see cref="SigningInput"/>. Null only on a fixture made by <see cref="CreateUnsignedForMalformedFixtures"/>: no text with a missing <c>g</c> parses.</param>
public sealed record BlindInvitation(
    string Address, Guid HubNodeId, string Trust, string SpkiPin, byte[] HubPublicKey,
    byte[] InviteId, byte[] Secret, long ExpiresAtUnix, string SourceName, byte[]? Signature)
{
    private const string Prefix = "bmb-invite:?";
    public const int Version = 1;
    public const int InviteIdSize = 16;
    public const int SecretSize = 32;
    public const int PinSize = 32;
    public const int SignatureSize = 64;
    public const int MaxSourceNameLength = 64;

    /// <summary>A longer text is not an invitation (the longest real one, a 64-letter Cyrillic name included, is under 1 100 characters).</summary>
    public const int MaxTextLength = 2048;

    private const long MaxUnixSeconds = 253402300799; // 9999-12-31T23:59:59Z

    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal) { "v", "a", "n", "t", "s", "k", "i", "x", "e", "d", "g" };

    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);

    public bool IsExpired(DateTimeOffset now) => now.ToUnixTimeSeconds() >= ExpiresAtUnix;

    /// <summary>SHA-256 of the secret: what the hub stores, what the signature covers, and all a device keeps of the secret after the redemption.</summary>
    public byte[] SecretHash() => SHA256.HashData(Secret);

    /// <summary>The invitation's id as it is written on the wire and in the hub's table.</summary>
    public string InviteIdText => Base64Url.EncodeToString(InviteId);

    /// <summary>The code both sides show for a redemption by this device (see <see cref="BlindInviteMatchCode"/>).</summary>
    public string MatchCodeFor(Guid nodeId, byte[] publicKey) => BlindInviteMatchCode.Compute(InviteId, nodeId, publicKey);

    /// <summary>
    /// An invitation for these values, signed by <paramref name="sign"/> over <see cref="SigningInput"/> (the hub passes its identity signer). The address is reduced to
    /// its origin like a call code's. What comes back verifies against <paramref name="hubPublicKey"/>, or this throws: a signer that signs with another key than the one
    /// the invitation names would make an invitation no device accepts.
    /// </summary>
    /// <exception cref="ArgumentException">A value that <see cref="TryParse"/> would refuse, or a signature that does not verify.</exception>
    public static BlindInvitation CreateSigned(string address, Guid hubNodeId, string trust, string? spkiPin, byte[] hubPublicKey,
        byte[] inviteId, byte[] secret, DateTimeOffset expiresAt, string sourceName, Func<byte[], byte[]> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);
        var unsigned = CreateUnsignedForMalformedFixtures(address, hubNodeId, trust, spkiPin, hubPublicKey, inviteId, secret, expiresAt, sourceName);
        var signed = unsigned with { Signature = sign(unsigned.SigningInput()) };
        return signed.SignatureIsValid() ? signed : throw new ArgumentException("The signature does not verify against the hub key named in the invitation.", nameof(sign));
    }

    /// <summary>
    /// An invitation WITHOUT the hub's signature, for tests that need a fixture the device must refuse (or one to sign by hand, field by field). Nothing that makes an
    /// invitation for a person calls this: <see cref="TryParse"/> refuses its text.
    /// </summary>
    /// <exception cref="ArgumentException">A value that <see cref="TryParse"/> would refuse.</exception>
    public static BlindInvitation CreateUnsignedForMalformedFixtures(string address, Guid hubNodeId, string trust, string? spkiPin, byte[] hubPublicKey,
        byte[] inviteId, byte[] secret, DateTimeOffset expiresAt, string sourceName)
    {
        var origin = CodeText.HttpsOrigin(address) ?? throw new ArgumentException("The address must be https://host[:port].", nameof(address));
        var invitation = new BlindInvitation(origin, hubNodeId, trust, spkiPin ?? "", hubPublicKey, inviteId, secret, expiresAt.ToUnixTimeSeconds(), sourceName.Trim(), null);
        return invitation.Problem() is { } why ? throw new ArgumentException(why) : invitation;
    }

    /// <summary>The same invitation carrying this signature over <see cref="SigningInput"/> (it may not verify: <see cref="SignatureIsValid"/> says).</summary>
    public BlindInvitation WithSignature(byte[] signature) => this with { Signature = signature };

    /// <summary>
    /// The bytes the hub signs: a domain label that carries the version (<c>bmb-invite-v1</c>: the only <c>v</c> this build reads) and every other field in a fixed order, the secret by its hash (a signature over the secret itself would be
    /// a second place it travels to; the hash of 32 random bytes tells nobody the secret). Fields cannot move into one another: none of them may contain a line break.
    /// </summary>
    public byte[] SigningInput() => SigningInputFor(Address, HubNodeId, Trust, SpkiPin, HubPublicKey, InviteId, SHA256.HashData(Secret), ExpiresAtUnix, SourceName);

    /// <summary><see cref="SigningInput"/> from the values and the secret's hash (what a device that keeps the hash and not the secret verifies the signature against).</summary>
    public static byte[] SigningInputFor(string address, Guid hubNodeId, string trust, string spkiPin, byte[] hubPublicKey, byte[] inviteId, byte[] secretHash, long expiresAtUnix, string sourceName) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "bmb-invite-v1", address, hubNodeId.ToString("D"), trust, spkiPin, Base64Url.EncodeToString(hubPublicKey), Base64Url.EncodeToString(inviteId),
            Base64Url.EncodeToString(secretHash), expiresAtUnix.ToString(CultureInfo.InvariantCulture), sourceName));

    /// <summary>
    /// What is wrong with an invitation's values as a device keeps them after the redemption (everything but the secret, which is kept as its hash): the same checks the text form
    /// passes, and the hub's signature over all of it, verified against the key the values name. Null when they are all right. A state value that was edited, or damaged, fails here.
    /// </summary>
    public static string? TargetProblem(string address, Guid hubNodeId, string trust, string spkiPin, byte[] hubPublicKey, byte[] inviteId, long expiresAtUnix, string sourceName,
        byte[]? secretHash, byte[]? signature)
    {
        var shape = new BlindInvitation(address, hubNodeId, trust, spkiPin, hubPublicKey, inviteId, new byte[SecretSize], expiresAtUnix, sourceName, null).Problem();
        if (shape is not null) return shape;
        if (secretHash is not { Length: 32 } || signature is not { Length: SignatureSize }) return "The invitation's signature is missing.";
        try
        {
            return Ed25519Signer.Verify(hubPublicKey, SigningInputFor(address, hubNodeId, trust, spkiPin, hubPublicKey, inviteId, secretHash, expiresAtUnix, sourceName), signature)
                ? null
                : "The invitation's signature does not verify.";
        }
        catch (Exception)
        {
            return "The invitation's signature does not verify.";
        }
    }

    /// <summary>True if <see cref="Signature"/> is the hub key's signature over <see cref="SigningInput"/>.</summary>
    public bool SignatureIsValid()
    {
        if (Signature is not { Length: SignatureSize } || HubPublicKey.Length != CryptoConstants.Ed25519PublicKeySize) return false;
        try { return Ed25519Signer.Verify(HubPublicKey, SigningInput(), Signature); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The text for a log, an exception or a debugger: every field but the secret, which reads <c>&lt;redacted&gt;</c>. An invitation is a bearer secret, and the framework
    /// formats an object by this method wherever it is interpolated or concatenated, so it must never carry the secret; <see cref="ToWireText"/> is the one that does.
    /// </summary>
    public override string ToString() => Prefix + string.Join("&", WireFields().Select(f => f.Key + "=" + (f.Key == "x" ? "<redacted>" : Uri.EscapeDataString(f.Value))));

    /// <summary>
    /// The invitation as the string the friend receives (the API's answer, the QR code): the ONLY formatter that writes the secret. Used where the string is handed to a person
    /// and in the codec's round-trip tests, nowhere else. An unsigned fixture (<see cref="CreateUnsignedForMalformedFixtures"/>) has no <c>g</c>, and its text is refused.
    /// </summary>
    public string ToWireText() => CodeText.Format(Prefix, WireFields().Select(f => (f.Key, (string?)f.Value)));

    private IEnumerable<(string Key, string Value)> WireFields()
    {
        yield return ("v", Version.ToString(CultureInfo.InvariantCulture));
        yield return ("a", Address);
        yield return ("n", HubNodeId.ToString("D"));
        yield return ("t", Trust);
        if (Trust == BlindTrust.Pin) yield return ("s", SpkiPin);
        yield return ("k", Base64Url.EncodeToString(HubPublicKey));
        yield return ("i", Base64Url.EncodeToString(InviteId));
        yield return ("x", Base64Url.EncodeToString(Secret));
        yield return ("e", ExpiresAtUnix.ToString(CultureInfo.InvariantCulture));
        yield return ("d", SourceName);
        if (Signature is not null) yield return ("g", Base64Url.EncodeToString(Signature));
    }

    /// <summary>Parses an invitation; expiry is a separate check (<see cref="IsExpired"/>). Never throws.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out BlindInvitation? invitation)
    {
        invitation = null;
        if (text is null || text.Length > MaxTextLength) return false;
        var fields = CodeText.Parse(text, Prefix);
        if (fields == null || fields.Keys.Any(k => !Keys.Contains(k))) return false;

        if (fields.GetValueOrDefault("v") != "1") return false;
        if (CodeText.HttpsOrigin(fields.GetValueOrDefault("a")) is not { } address) return false;
        if (!Guid.TryParseExact(fields.GetValueOrDefault("n"), "D", out var hubNodeId)) return false;
        var trust = fields.GetValueOrDefault("t");
        if (!BlindTrust.IsKnown(trust)) return false;
        var pin = fields.GetValueOrDefault("s");
        if (trust == BlindTrust.Pin)
        {
            if (pin is null || CodeText.Base64UrlOfLength(pin, PinSize) == null) return false;
        }
        else if (pin is not null)
        {
            // A pin next to "normal certificate" says two things about the same hub.
            return false;
        }
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("k"), CryptoConstants.Ed25519PublicKeySize) is not { } key) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("i"), InviteIdSize) is not { } inviteId) return false;
        if (!long.TryParse(fields.GetValueOrDefault("e"), NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt)) return false;
        var name = fields.GetValueOrDefault("d");
        // Trimmed text is what is signed and shown: a name with spaces around it is refused rather than silently changed.
        if (name is null || name != name.Trim()) return false;
        // Required: without the hub's signature the address, the pin and the key are three unrelated claims (anyone could change the address and keep the rest).
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("g"), SignatureSize) is not { } signature) return false;
        // The secret is decoded last: no refusal above leaves a decoded copy of it behind, and the ones below wipe it.
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("x"), SecretSize) is not { } secret) return false;

        var parsed = new BlindInvitation(address, hubNodeId, trust!, pin ?? "", key, inviteId, secret, expiresAt, name, signature);
        if (parsed.Problem() is not null || !parsed.SignatureIsValid())
        {
            // A refused string is not kept: its secret does not outlive this call.
            CryptographicOperations.ZeroMemory(secret);
            return false;
        }
        invitation = parsed;
        return true;
    }

    /// <summary>What is wrong with the values themselves (the checks the text form shares with the factories), or null.</summary>
    private string? Problem()
    {
        if (CodeText.HttpsOrigin(Address) != Address) return "The address must be https://host[:port].";
        if (Uri.TryCreate(Address, UriKind.Absolute, out var uri) && PeerNetworkRules.HostRefusal(uri.DnsSafeHost) is { } hostRefusal) return hostRefusal;
        if (HubNodeId == Guid.Empty || BlindNodeId.IsBlind(HubNodeId)) return "An invitation names an ordinary node: a blind node cannot take a redemption.";
        if (Trust == BlindTrust.Pin ? CodeText.Base64UrlOfLength(SpkiPin, PinSize) == null : Trust != BlindTrust.PublicCa || SpkiPin.Length != 0)
            return "The pin must be a SHA-256 for a pinned hub and absent for a hub on a public certificate.";
        if (HubPublicKey.Length != CryptoConstants.Ed25519PublicKeySize) return "The hub key must be an Ed25519 public key.";
        if (InviteId.Length != InviteIdSize || Secret.Length != SecretSize) return "The invitation id and secret have fixed lengths.";
        if (ExpiresAtUnix is < 1 or > MaxUnixSeconds) return "The expiry is not a date.";
        if (!BlindInviteText.IsPlain(SourceName, MaxSourceNameLength)) return "The name must be 1 to 64 characters without control characters.";
        if (Signature is not null && Signature.Length != SignatureSize) return "The signature must be 64 bytes.";
        return null;
    }
}

/// <summary>
/// The short code the device and the owner's page both show for one redemption, so the owner can confirm "this is my friend's device" and not whoever redeemed
/// first: the first 30 bits of <c>SHA-256("bmb-invite-match-v1" | inviteId | nodeId | publicKey)</c> in Crockford base32, written <c>K7F-2QX</c>. The inputs are
/// fixed-size and concatenated as they are: the label in UTF-8, the 16 id bytes, the node id as its 36-character lower-case <c>D</c> text in UTF-8 (never
/// <see cref="Guid.ToByteArray"/>, whose byte order differs between implementations) and the 32 key bytes. 30 bits is not a defence against a search for a
/// collision on its own; it is what a person reads aloud, and the redemption it describes is already bound to one invitation and one key by the proofs.
/// </summary>
public static class BlindInviteMatchCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly byte[] Label = "bmb-invite-match-v1"u8.ToArray();

    public static string Compute(byte[] inviteId, Guid nodeId, byte[] publicKey)
    {
        var id = Encoding.UTF8.GetBytes(nodeId.ToString("D"));
        var input = new byte[Label.Length + inviteId.Length + id.Length + publicKey.Length];
        var at = 0;
        foreach (var part in new[] { Label, inviteId, id, publicKey })
        {
            part.CopyTo(input, at);
            at += part.Length;
        }
        var hash = SHA256.HashData(input);
        var bits = ((uint)hash[0] << 24 | (uint)hash[1] << 16 | (uint)hash[2] << 8 | hash[3]) >> 2;
        var chars = new char[7];
        for (var i = 0; i < 6; i++)
            chars[i < 3 ? i : i + 1] = Alphabet[(int)(bits >> 5 * (5 - i)) & 31];
        chars[3] = '-';
        return new string(chars);
    }

    /// <summary>
    /// The canonical <c>K7F-2QX</c> form of a code a person typed (case, a missing dash, spaces, and Crockford's O/0 and I,L/1 do not matter), or null.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null || typed.Length > 32) return null;
        var chars = new List<char>(6);
        foreach (var c in typed)
        {
            if (c is '-' or ' ') continue;
            var up = char.ToUpperInvariant(c);
            up = up switch { 'O' => '0', 'I' or 'L' => '1', _ => up };
            if (Alphabet.IndexOf(up) < 0) return null;
            chars.Add(up);
        }
        return chars.Count == 6 ? string.Concat(chars[..3]) + "-" + string.Concat(chars[3..]) : null;
    }
}

/// <summary>The plain-text rule for names and labels in invitations: shown on another person's screen, so no control or formatting characters.</summary>
public static class BlindInviteText
{
    public static bool IsPlain(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && value == value.Trim()
        && value.All(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format);
}
