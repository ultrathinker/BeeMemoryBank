using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>
/// What Windows seals under the DEK as <c>android-backup:&lt;phone id&gt;</c> when it pairs an Android blind
/// node (plan section 10): the phone's backup key AND the node the phone was told to call — the producer of
/// the blind package inside every backup body. A restore takes the producer's identity from here, never from
/// the backup file: the phone holds the backup key, so a compromised phone can write any body, but it cannot
/// forge the listener's signature, nor change what is sealed under the DEK.
///
/// <para>The sealed secret itself is an any-peer event (last writer wins by name), and every full node holds
/// the DEK — so being under the DEK proves nothing about who chose the producer. The producer binding is
/// therefore signed by the superadmin that paired the phone (<see cref="PairedBy"/>, over
/// <see cref="PairingStatement"/>); a restore accepts it only from a superadmin a DEK anchor vouches for. The
/// signature is inside the sealed value, so re-sealing after a rotation carries it forward unchanged.</para>
/// </summary>
/// <param name="ProducerAddress">The listener's https origin; the restored device dials it pinned.</param>
/// <param name="ProducerTlsSpki">The listener's TLS pin, as the network recorded it.</param>
/// <param name="PairedBy">The node that paired the phone and signed the binding.</param>
/// <param name="PairingSignature">Ed25519 by <paramref name="PairedBy"/> over <see cref="PairingStatement"/>.</param>
public sealed record BlindPhoneBackupSeal(
    byte[] BackupKey, Guid ProducerNodeId, byte[] ProducerPublicKey, string ProducerAddress, string ProducerTlsSpki,
    Guid PairedBy, byte[] PairingSignature)
{
    private const int KeySize = 32;
    private const int SignatureSize = 64;

    /// <summary>
    /// What the pairing superadmin signs: this phone, the SHA-256 of its backup key, and the node it calls with
    /// that node's key, address and pin. The key itself is secret and stays out; its digest is bound so that a
    /// peer re-publishing the record cannot keep the signature and swap only the key (every real backup would
    /// then stop opening, and a body under the swapped key would pass as the paired one).
    /// </summary>
    public byte[] PairingStatement(Guid phoneId) => Encoding.UTF8.GetBytes(
        $"bmb-android-pairing-v2\n{phoneId:D}\n{Convert.ToHexStringLower(SHA256.HashData(BackupKey))}\n{ProducerNodeId:D}\n" +
        $"{Base64Url.EncodeToString(ProducerPublicKey)}\n{ProducerAddress}\n{ProducerTlsSpki}");

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new Wire(2, Base64Url.EncodeToString(BackupKey),
        ProducerNodeId, Base64Url.EncodeToString(ProducerPublicKey), ProducerAddress, ProducerTlsSpki,
        PairedBy, Base64Url.EncodeToString(PairingSignature)));

    /// <summary>Strict: the version, the keys' and signature's sizes, an https origin and a well-formed pin — or false.</summary>
    public static bool TryDecode(byte[]? value, [NotNullWhen(true)] out BlindPhoneBackupSeal? seal)
    {
        seal = null;
        Wire? wire;
        try { wire = value == null ? null : JsonSerializer.Deserialize<Wire>(value); }
        catch (JsonException) { return false; }
        if (wire is not { V: 2 } || wire.Producer == Guid.Empty || wire.PairedBy == Guid.Empty) return false;
        if (CodeText.Base64UrlOfLength(wire.Key, KeySize) is not { } key) return false;
        if (CodeText.Base64UrlOfLength(wire.ProducerKey, KeySize) is not { } producerKey) return false;
        if (CodeText.Base64UrlOfLength(wire.Signature, SignatureSize) is not { } signature) return false;
        if (!BlindListener.IsCallable(wire.Address, wire.Spki)) return false;
        seal = new BlindPhoneBackupSeal(key, wire.Producer, producerKey, wire.Address!, wire.Spki!, wire.PairedBy, signature);
        return true;
    }

    private sealed record Wire(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("producer")] Guid Producer,
        [property: JsonPropertyName("producer_key")] string? ProducerKey,
        [property: JsonPropertyName("address")] string? Address,
        [property: JsonPropertyName("spki")] string? Spki,
        [property: JsonPropertyName("paired_by")] Guid PairedBy,
        [property: JsonPropertyName("signature")] string? Signature);
}

/// <summary>
/// Whether a node can be the one an Android blind node calls: exactly what <see cref="BlindCallCode"/> accepts —
/// a path-less https origin and a 32-byte base64url SPKI pin. One check for listing listeners, pairing and the
/// sealed pairing record, so none of them lets through a row the phone would then refuse.
/// </summary>
public static class BlindListener
{
    public static bool IsCallable(string? address, string? tlsSpki, byte[]? publicKey = null) =>
        CodeText.HttpsOrigin(address) is not null
        && CodeText.Base64UrlOfLength(tlsSpki, 32) is not null
        && (publicKey == null || publicKey.Length == CryptoConstants.Ed25519PublicKeySize);
}
