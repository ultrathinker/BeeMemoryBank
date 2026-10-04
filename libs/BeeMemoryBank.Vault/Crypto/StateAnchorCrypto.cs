using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// The MAC of an integrity anchor (BMB-43, plan 5.5): HMAC-SHA256 under K_state = HKDF-SHA256(DEK,
/// info "bmb-blind-state-v1") over the canonical form of the anchor's fields. Only a holder of the DEK
/// can produce it; a blind node stores and relays it; a restore checks it with the DEK it recovered.
/// </summary>
public static class StateAnchorCrypto
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("bmb-blind-state-v1");

    public static string ComputeHmac(
        byte[] dek, string anchorId, string dekFingerprint, IReadOnlyDictionary<string, long> positionVector,
        string digest, string createdAt)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, CryptoConstants.KeySize, salt: null, info: Info);
        try
        {
            var canonical = Encoding.UTF8.GetBytes(Canonical(anchorId, dekFingerprint, positionVector, digest, createdAt));
            return Convert.ToHexStringLower(HMACSHA256.HashData(key, canonical));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool Verify(
        byte[] dek, string anchorId, string dekFingerprint, IReadOnlyDictionary<string, long> positionVector,
        string digest, string createdAt, string hmac)
    {
        var expected = ComputeHmac(dek, anchorId, dekFingerprint, positionVector, digest, createdAt);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(hmac ?? ""));
    }

    /// <summary>
    /// One field per line; the vector as compact JSON with keys uppercased and sorted ordinally, so the
    /// JSON a peer happened to serialise never changes what is signed.
    /// </summary>
    public static string Canonical(
        string anchorId, string dekFingerprint, IReadOnlyDictionary<string, long> positionVector, string digest, string createdAt) =>
        string.Join('\n',
            anchorId.ToLowerInvariant(),
            dekFingerprint,
            CanonicalVector(positionVector),
            digest,
            createdAt);

    public static string CanonicalVector(IReadOnlyDictionary<string, long> positionVector) =>
        JsonSerializer.Serialize(new SortedDictionary<string, long>(
            positionVector.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value), StringComparer.Ordinal));
}
