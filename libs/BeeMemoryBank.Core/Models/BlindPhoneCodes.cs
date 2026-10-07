using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Models;

/// <summary>
/// First of the two codes that pair an Android blind node (plan section 10): the phone shows it,
/// Windows reads it and records the phone with <c>whitelist_add</c>. It carries the phone's blind
/// NodeId, its Ed25519 public key, the one-time pairing secret (<see cref="BlindPairingSecret"/>) that
/// authenticates Windows' answer, and the phone's backup key, which Windows seals under the DEK as
/// <c>android-backup:&lt;node id&gt;</c> so the phone's backups open later with the master password.
///
/// <para>Text form: <c>bmb-blind-phone:?n=&lt;node id&gt;&amp;k=&lt;public key&gt;&amp;s=&lt;secret&gt;&amp;b=&lt;backup key&gt;&amp;d=&lt;name&gt;</c>,
/// keys and secrets base64url.</para>
/// </summary>
public sealed record BlindPhoneCode(Guid NodeId, byte[] PublicKey, byte[] Secret, byte[] BackupKey, string DisplayName)
{
    private const string Prefix = "bmb-blind-phone:?";
    public const int MaxDisplayNameLength = 64;

    public override string ToString() => CodeText.Format(Prefix,
    [
        ("n", NodeId.ToString("D")),
        ("k", Base64Url.EncodeToString(PublicKey)),
        ("s", Base64Url.EncodeToString(Secret)),
        ("b", Base64Url.EncodeToString(BackupKey)),
        ("d", DisplayName),
    ]);

    /// <summary>
    /// Parses a phone code. The NodeId must carry the blind mark — a code for an ordinary id would
    /// make Windows record a full member that holds no DEK.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out BlindPhoneCode? code)
    {
        code = null;
        var fields = CodeText.Parse(text, Prefix);
        if (fields == null) return false;

        if (!Guid.TryParseExact(fields.GetValueOrDefault("n"), "D", out var nodeId) || !BlindNodeId.IsBlind(nodeId)) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("k"), CryptoConstants.Ed25519PublicKeySize) is not { } key) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("s"), BlindPairingSecret.Size) is not { } secret) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("b"), 32) is not { } backupKey) return false;
        var name = fields.GetValueOrDefault("d")?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength || name.Any(char.IsControl)) return false;

        code = new BlindPhoneCode(nodeId, key, secret, backupKey, name);
        return true;
    }
}

/// <summary>
/// Second pairing code, Windows → phone: where the Android blind node calls — the listening node's
/// https address, NodeId, how to trust its TLS endpoint (<see cref="BlindTrust"/>: the SPKI pin of its TLS
/// key, or the normal certificate chain) and its Ed25519 public key (the minimal whitelist the phone needs to
/// check the blind package's signature). Windows authenticates the code with a MAC under the pairing secret
/// from the <see cref="BlindPhoneCode"/>, so only the Windows that read this phone's code can tell it where
/// to call.
///
/// <para>Text form, pinned node (what every code was until ADR 0007, and still is for a pinned node, so old
/// and new apps read it alike): <c>bmb-blind-call:?a=&lt;https address&gt;&amp;n=&lt;node id&gt;&amp;s=&lt;pin&gt;&amp;k=&lt;public key&gt;&amp;m=&lt;mac&gt;</c>.
/// Node on a public CA: <c>bmb-blind-call:?a=...&amp;n=...&amp;t=public-ca&amp;k=...&amp;m=...</c> — no pin, and a MAC of
/// its own label that includes the trust; an app that predates the mode finds no pin and refuses the code.</para>
/// </summary>
/// <param name="SpkiPin">The pin; empty when <paramref name="Trust"/> is <see cref="BlindTrust.PublicCa"/>.</param>
/// <param name="Trust"><see cref="BlindTrust.Pin"/> or <see cref="BlindTrust.PublicCa"/>.</param>
public sealed record BlindCallCode(string Address, Guid NodeId, string SpkiPin, byte[] PublicKey, byte[] Mac, string Trust = BlindTrust.Pin)
{
    private const string Prefix = "bmb-blind-call:?";

    /// <summary>A code for these values, authenticated with <paramref name="secret"/>.</summary>
    public static BlindCallCode Create(string address, Guid nodeId, string spkiPin, byte[] publicKey, byte[] secret)
    {
        var origin = CodeText.HttpsOrigin(address) ?? throw new ArgumentException("The address must be https://host[:port].", nameof(address));
        return new BlindCallCode(origin, nodeId, spkiPin, publicKey,
            BlindPairingSecret.CallCodeMac(secret, MacInput(origin, nodeId, BlindTrust.Pin, spkiPin, publicKey)));
    }

    /// <summary>A code for a node whose certificate the system's chain vouches for, authenticated with <paramref name="secret"/>.</summary>
    public static BlindCallCode CreatePublicCa(string address, Guid nodeId, byte[] publicKey, byte[] secret)
    {
        var origin = CodeText.HttpsOrigin(address) ?? throw new ArgumentException("The address must be https://host[:port].", nameof(address));
        return new BlindCallCode(origin, nodeId, "", publicKey,
            BlindPairingSecret.CallCodeMac(secret, MacInput(origin, nodeId, BlindTrust.PublicCa, "", publicKey)), BlindTrust.PublicCa);
    }

    /// <summary>True if the code was made with <paramref name="secret"/> — the phone's own.</summary>
    public bool IsAuthenticBy(byte[] secret) =>
        BlindPairingSecret.VerifyCallCodeMac(secret, MacInput(Address, NodeId, Trust, SpkiPin, PublicKey), Mac);

    public override string ToString() => CodeText.Format(Prefix,
    [
        ("a", Address),
        ("n", NodeId.ToString("D")),
        // A pinned code is written as it always was; only the new mode names itself.
        ("t", Trust == BlindTrust.PublicCa ? BlindTrust.PublicCa : null),
        ("s", Trust == BlindTrust.PublicCa ? null : SpkiPin),
        ("k", Base64Url.EncodeToString(PublicKey)),
        ("m", Base64Url.EncodeToString(Mac)),
    ]);

    /// <summary>Parses a call code; authenticity is a separate check (<see cref="IsAuthenticBy"/>).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out BlindCallCode? code)
    {
        code = null;
        var fields = CodeText.Parse(text, Prefix);
        if (fields == null) return false;

        if (CodeText.HttpsOrigin(fields.GetValueOrDefault("a")) is not { } address) return false;
        if (!Guid.TryParseExact(fields.GetValueOrDefault("n"), "D", out var nodeId)) return false;
        // No "t" is a pinned node (every code before the mode existed). A mode this build does not know
        // refuses the code: guessing at how to trust a node is what a code must never leave to chance.
        var trust = fields.GetValueOrDefault("t") ?? BlindTrust.Pin;
        if (!BlindTrust.IsKnown(trust)) return false;
        var pin = fields.GetValueOrDefault("s");
        if (trust == BlindTrust.Pin)
        {
            // The pin itself is checked by SpkiPin at connect time; here only that it is a SHA-256.
            if (pin is null || CodeText.Base64UrlOfLength(pin, 32) == null) return false;
        }
        else if (pin is not null)
        {
            // A pin next to "normal certificate" says two things about the same node.
            return false;
        }
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("k"), CryptoConstants.Ed25519PublicKeySize) is not { } key) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("m"), 32) is not { } mac) return false;

        code = new BlindCallCode(address, nodeId, pin ?? "", key, mac, trust);
        return true;
    }

    // Every field, in a fixed order with a domain label, so no field can be moved into another. A pinned code keeps
    // the label and fields it had; the new mode has a label of its own, so one cannot pass for the other.
    private static byte[] MacInput(string address, Guid nodeId, string trust, string pin, byte[] publicKey) =>
        trust == BlindTrust.PublicCa
            ? Encoding.UTF8.GetBytes($"bmb-blind-call-v2\n{address}\n{nodeId:D}\n{trust}\n{Base64Url.EncodeToString(publicKey)}")
            : Encoding.UTF8.GetBytes($"bmb-blind-call-v1\n{address}\n{nodeId:D}\n{pin}\n{Base64Url.EncodeToString(publicKey)}");
}
