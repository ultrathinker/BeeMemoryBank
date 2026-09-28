using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// How the first seed names its seeder (plan 4.2, review L-stage1 #3). The node that holds a pair
/// code sends its node id and Ed25519 public key with an HMAC over them and the seed id, under a key
/// derived from the one-time pairing secret. The blind node checks the MAC before it reads a byte of
/// the package, and then accepts only a package signed by exactly that key — the package's own
/// manifest never chooses the key it is verified against. The secret itself never travels: a relay
/// that sees the request learns nothing it could use for a seed of its own.
/// </summary>
public static class BlindSeederProof
{
    public const string NodeIdHeader = "X-BMB-Seeder-Node-Id";
    public const string KeyHeader = "X-BMB-Seeder-Key";
    public const string MacHeader = "X-BMB-Seeder-Mac";

    private static readonly byte[] Label = "bmb-blind-seeder-v1"u8.ToArray();
    private const int Ed25519KeyLength = 32;

    /// <summary>The MAC (base64) the seeder sends for <paramref name="seedId"/>.</summary>
    public static string Compute(string pairingSecret, Guid seedId, Guid seederNodeId, byte[] seederPublicKey)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(pairingSecret), 32, info: Label);
        try
        {
            return Convert.ToBase64String(HMACSHA256.HashData(key, Message(seedId, seederNodeId, seederPublicKey)));
        }
        finally
        {
            Array.Clear(key);
        }
    }

    /// <summary>True if <paramref name="macB64"/> is the MAC of this seeder and key for this seed. Constant time.</summary>
    public static bool Verify(string pairingSecret, Guid seedId, Guid seederNodeId, string seederPublicKeyB64, string macB64)
    {
        byte[] publicKey, presented;
        try
        {
            publicKey = Convert.FromBase64String(seederPublicKeyB64);
            presented = Convert.FromBase64String(macB64);
        }
        catch (FormatException)
        {
            return false;
        }
        if (publicKey.Length != Ed25519KeyLength) return false;
        var expected = Convert.FromBase64String(Compute(pairingSecret, seedId, seederNodeId, publicKey));
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    private static byte[] Message(Guid seedId, Guid seederNodeId, byte[] seederPublicKey) =>
        [.. Label, 0, .. seedId.ToByteArray(), .. seederNodeId.ToByteArray(), .. seederPublicKey];
}
