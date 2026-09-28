using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// The restore code a blind node's console issues (plan 6.7), in the form of its pair code: who the blind
/// node is — NodeId and a fingerprint of its Ed25519 key — where to reach it, the pin of its TLS key, and the
/// one-time secret the restore routes take. The restore trusts the package only if it comes signed by exactly
/// that key, from exactly that node: the key in the response is checked against the code, never the other way.
///
/// <para>Text form: <c>BMBRESTORE1.</c> + base64url(JSON), copied or scanned from the blind node's console.</para>
/// </summary>
/// <param name="KeyFingerprint">base64url(SHA-256(Ed25519 public key)), unpadded.</param>
/// <param name="Address">The blind node's public address, when it knows it (BMB_PUBLIC_ADDRESS).</param>
public sealed record BlindRestoreCode(
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("key_fp")] string KeyFingerprint,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("tls_spki")] string TlsSpki,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("expires_at")] DateTime ExpiresAt)
{
    public const string Prefix = "BMBRESTORE1.";

    public static string FingerprintOf(byte[] publicKey) => Base64Url.EncodeToString(SHA256.HashData(publicKey));

    /// <summary>Is <paramref name="publicKey"/> the key this code names (constant time)?</summary>
    public bool IsKeyOf(byte[] publicKey) => Spki.Equal(FingerprintOf(publicKey), KeyFingerprint);

    public override string ToString() => Prefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>Parses a pasted code; refuses anything that is not a complete code naming a blind node.</summary>
    public static BlindRestoreCode Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("Not a restore code: copy it from the blind node's console.");
        BlindRestoreCode? code;
        try
        {
            code = JsonSerializer.Deserialize<BlindRestoreCode>(Base64Url.DecodeFromChars(trimmed.AsSpan(Prefix.Length)));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new FormatException("The restore code is damaged.", ex);
        }
        if (code is null || !BlindNodeId.IsBlind(code.NodeId))
            throw new FormatException("The restore code does not name a blind node.");
        if (!RestoreTlsPin.IsWellFormed(code.KeyFingerprint) || !RestoreTlsPin.IsWellFormed(code.TlsSpki) || string.IsNullOrEmpty(code.Secret))
            throw new FormatException("The restore code is incomplete.");
        if (code.Address is { } a && (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new FormatException("The restore code carries an address that is not HTTPS.");
        return code;
    }
}
