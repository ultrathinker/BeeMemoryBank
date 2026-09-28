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
/// https address, NodeId, the SPKI pin of its TLS key and its Ed25519 public key (the minimal
/// whitelist the phone needs to check the blind package's signature). Windows authenticates the code
/// with a MAC under the pairing secret from the <see cref="BlindPhoneCode"/>, so only the Windows that
/// read this phone's code can tell it where to call.
///
/// <para>Text form: <c>bmb-blind-call:?a=&lt;https address&gt;&amp;n=&lt;node id&gt;&amp;s=&lt;pin&gt;&amp;k=&lt;public key&gt;&amp;m=&lt;mac&gt;</c>.</para>
/// </summary>
public sealed record BlindCallCode(string Address, Guid NodeId, string SpkiPin, byte[] PublicKey, byte[] Mac)
{
    private const string Prefix = "bmb-blind-call:?";

    /// <summary>A code for these values, authenticated with <paramref name="secret"/>.</summary>
    public static BlindCallCode Create(string address, Guid nodeId, string spkiPin, byte[] publicKey, byte[] secret)
    {
        var origin = CodeText.HttpsOrigin(address) ?? throw new ArgumentException("The address must be https://host[:port].", nameof(address));
        return new BlindCallCode(origin, nodeId, spkiPin, publicKey,
            BlindPairingSecret.CallCodeMac(secret, MacInput(origin, nodeId, spkiPin, publicKey)));
    }

    /// <summary>True if the code was made with <paramref name="secret"/> — the phone's own.</summary>
    public bool IsAuthenticBy(byte[] secret) =>
        BlindPairingSecret.VerifyCallCodeMac(secret, MacInput(Address, NodeId, SpkiPin, PublicKey), Mac);

    public override string ToString() => CodeText.Format(Prefix,
    [
        ("a", Address),
        ("n", NodeId.ToString("D")),
        ("s", SpkiPin),
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
        // The pin itself is checked by SpkiPin at connect time; here only that it is a SHA-256.
        if (fields.GetValueOrDefault("s") is not { } pin || CodeText.Base64UrlOfLength(pin, 32) == null) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("k"), CryptoConstants.Ed25519PublicKeySize) is not { } key) return false;
        if (CodeText.Base64UrlOfLength(fields.GetValueOrDefault("m"), 32) is not { } mac) return false;

        code = new BlindCallCode(address, nodeId, pin, key, mac);
        return true;
    }

    // Every field, in a fixed order with a domain label, so no field can be moved into another.
    private static byte[] MacInput(string address, Guid nodeId, string pin, byte[] publicKey) =>
        Encoding.UTF8.GetBytes($"bmb-blind-call-v1\n{address}\n{nodeId:D}\n{pin}\n{Base64Url.EncodeToString(publicKey)}");
}
