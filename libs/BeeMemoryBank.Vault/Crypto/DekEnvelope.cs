using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Per-peer X25519 envelopes for a confidential DEK rotation (ADR 0006).
///
/// <para>
/// The new master DEK is wrapped once per currently-trusted peer, each envelope openable ONLY by
/// that peer's X25519 private key. Both the peer's public key and its private scalar are DERIVED
/// from the Ed25519 identity keys that already exist on every node (node identity seed) and in every
/// whitelist row (peer Ed25519 public key) — no new key material is generated, distributed, or
/// joined. This is what lets a revoked node be excluded from a rotation it never received an
/// envelope for, closing the "new DEK is only ever wrapped under the old DEK" hole.
/// </para>
///
/// <para>
/// Key derivation follows the standard libsodium birational maps:
/// <list type="bullet">
///   <item><c>crypto_sign_ed25519_sk_to_curve25519</c>: SHA-512 the 32-byte seed, take the low 32
///   bytes, clamp — the X25519 private scalar.</item>
///   <item><c>crypto_sign_ed25519_pk_to_curve25519</c>: decode the Edwards y coordinate and map it
///   to the Montgomery u coordinate, u = (1 + y) / (1 - y) mod p — the X25519 public key.</item>
/// </list>
/// The identity <c>pk_to_curve(seed·B) == base·sk_to_curve(seed)</c> is what guarantees the
/// initiator (deriving the peer's public from its Ed25519 public) and the receiver (deriving its own
/// private from its Ed25519 seed) agree on the same shared secret.
/// </para>
/// </summary>
public static class DekEnvelope
{
    // Ed25519 / Curve25519 share the prime field p = 2^255 - 19.
    private static readonly BigInteger P =
        BigInteger.Pow(2, 255) - 19;

    private static readonly byte[] InfoPrefix = "bmb-dek-rotation-v1"u8.ToArray();

    /// <summary>A single peer's sealed copy of the new DEK.</summary>
    public sealed record Box(string WrappedB64, string NonceB64);

    /// <summary>
    /// The whole envelope set for one rotation: the rotation's ephemeral X25519 public key plus one
    /// <see cref="Box"/> per recipient, keyed by the recipient's UPPERCASE node id.
    /// </summary>
    public sealed record Set(string EphemeralPublicKeyB64, Dictionary<string, Box> Peers);

    /// <summary>A rotation recipient: a node id and that node's Ed25519 identity public key.</summary>
    public readonly record struct Recipient(Guid NodeId, byte[] Ed25519PublicKey);

    /// <summary>
    /// Builds the envelope set for a rotation. One ephemeral X25519 keypair is generated for the whole
    /// rotation; for each recipient the shared secret is X25519(ephemeral_priv, curve25519(peer_ed_pub)),
    /// the wrap key is HKDF-SHA256 over that shared secret salted by the rotation id and bound to the
    /// recipient's node id, and the DEK is sealed with AES-256-GCM under that wrap key with the node id
    /// as AAD.
    /// </summary>
    public static Set Build(byte[] newDek, Guid rotationEventId, IEnumerable<Recipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(newDek);
        ArgumentNullException.ThrowIfNull(recipients);

        var ephemeralSeed = SecureRandom.GetBytes(CryptoConstants.KeySize);
        var ephemeralPriv = new X25519PrivateKeyParameters(ephemeralSeed, 0);
        var ephemeralPubB64 = Convert.ToBase64String(ephemeralPriv.GeneratePublicKey().GetEncoded());

        var salt = SaltBytes(rotationEventId);
        var peers = new Dictionary<string, Box>(StringComparer.Ordinal);

        try
        {
            foreach (var recipient in recipients)
            {
                var nodeKey = NodeKey(recipient.NodeId);
                if (peers.ContainsKey(nodeKey))
                    continue; // a node id can only appear once — first writer wins.

                var peerX25519Pub = Ed25519PublicKeyToX25519PublicKey(recipient.Ed25519PublicKey);
                var shared = new byte[CryptoConstants.KeySize];
                ephemeralPriv.GenerateSecret(new X25519PublicKeyParameters(peerX25519Pub, 0), shared, 0);

                var wrapKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, CryptoConstants.KeySize, salt, InfoBytes(nodeKey));
                Array.Clear(shared);
                try
                {
                    var aad = Encoding.UTF8.GetBytes(nodeKey);
                    var (wrapped, nonce) = AesGcmHelper.Encrypt(wrapKey, newDek, aad);
                    peers[nodeKey] = new Box(Convert.ToBase64String(wrapped), Convert.ToBase64String(nonce));
                }
                finally
                {
                    Array.Clear(wrapKey);
                }
            }
        }
        finally
        {
            // The ephemeral private scalar is single-rotation, but clear our copy of the seed once
            // every envelope is sealed rather than leaving it on the heap until GC.
            Array.Clear(ephemeralSeed);
        }

        return new Set(ephemeralPubB64, peers);
    }

    /// <summary>
    /// Opens one recipient's envelope. Derives the recipient's X25519 private scalar from its Ed25519
    /// seed, recomputes the shared secret against the rotation's ephemeral public key, re-derives the
    /// wrap key with the same salt/info/AAD, and AES-GCM-opens the sealed DEK.
    ///
    /// <para>Throws <see cref="CryptographicException"/> (AES-GCM tag mismatch) if the envelope was
    /// sealed for a different node id, under a different rotation id, or was tampered with — the wrap
    /// key or the AAD then no longer matches.</para>
    /// </summary>
    public static byte[] Open(
        string ephemeralPublicKeyB64,
        string wrappedB64,
        string nonceB64,
        Guid rotationEventId,
        Guid myNodeId,
        byte[] myEd25519Seed)
    {
        ArgumentNullException.ThrowIfNull(ephemeralPublicKeyB64);
        ArgumentNullException.ThrowIfNull(wrappedB64);
        ArgumentNullException.ThrowIfNull(nonceB64);
        ArgumentNullException.ThrowIfNull(myEd25519Seed);

        var myPriv = Ed25519SeedToX25519PrivateKey(myEd25519Seed);
        try
        {
            var privParams = new X25519PrivateKeyParameters(myPriv, 0);
            var ephemeralPub = new X25519PublicKeyParameters(Convert.FromBase64String(ephemeralPublicKeyB64), 0);

            var shared = new byte[CryptoConstants.KeySize];
            privParams.GenerateSecret(ephemeralPub, shared, 0);

            var nodeKey = NodeKey(myNodeId);
            var wrapKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256, shared, CryptoConstants.KeySize, SaltBytes(rotationEventId), InfoBytes(nodeKey));
            Array.Clear(shared);
            try
            {
                var aad = Encoding.UTF8.GetBytes(nodeKey);
                return AesGcmHelper.Decrypt(
                    wrapKey, Convert.FromBase64String(wrappedB64), Convert.FromBase64String(nonceB64), aad);
            }
            finally
            {
                Array.Clear(wrapKey);
            }
        }
        finally
        {
            Array.Clear(myPriv);
        }
    }

    /// <summary>
    /// <c>crypto_sign_ed25519_sk_to_curve25519</c>: derive the X25519 private scalar from the 32-byte
    /// Ed25519 seed. SHA-512 the seed, take the low 32 bytes, clamp. Caller owns the returned buffer.
    /// </summary>
    public static byte[] Ed25519SeedToX25519PrivateKey(byte[] ed25519Seed)
    {
        ArgumentNullException.ThrowIfNull(ed25519Seed);
        if (ed25519Seed.Length != CryptoConstants.Ed25519PrivateKeySize)
            throw new ArgumentException(
                $"Ed25519 seed must be {CryptoConstants.Ed25519PrivateKeySize} bytes.", nameof(ed25519Seed));

        var h = SHA512.HashData(ed25519Seed);
        try
        {
            var scalar = new byte[CryptoConstants.KeySize];
            Array.Copy(h, 0, scalar, 0, CryptoConstants.KeySize);
            scalar[0] &= 248;
            scalar[31] &= 127;
            scalar[31] |= 64;
            return scalar;
        }
        finally
        {
            Array.Clear(h);
        }
    }

    /// <summary>
    /// <c>crypto_sign_ed25519_pk_to_curve25519</c>: map an Ed25519 public key to its X25519 public key.
    /// The Ed25519 encoding is the little-endian Edwards y coordinate with the x sign in the top bit;
    /// the Montgomery u coordinate is u = (1 + y) / (1 - y) mod p.
    /// </summary>
    public static byte[] Ed25519PublicKeyToX25519PublicKey(byte[] ed25519PublicKey)
    {
        ArgumentNullException.ThrowIfNull(ed25519PublicKey);
        if (ed25519PublicKey.Length != CryptoConstants.Ed25519PublicKeySize)
            throw new ArgumentException(
                $"Ed25519 public key must be {CryptoConstants.Ed25519PublicKeySize} bytes.", nameof(ed25519PublicKey));

        // Reject anything that is not a canonical Ed25519 point in the prime-order subgroup BEFORE
        // deriving an X25519 key from it. Without this, a garbage / off-curve / small-order 32-byte
        // value still produces *some* Montgomery-u: the birational map is just field arithmetic and
        // only rejects the y==1 singularity below. Two consequences it closes: (1) a corrupt or
        // maliciously-planted whitelist key would seal an envelope no one can open, silently locking
        // that peer out of every future DEK — the broken-peer path (DekRotationService.Propose)
        // catches this CryptographicException and EXCLUDES the peer with a log instead; (2) a
        // small-order public key would force a low-order shared secret. ValidatePublicKeyFull checks
        // canonical encoding, on-curve, and prime-order-subgroup membership in one call.
        if (!Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(ed25519PublicKey, 0))
            throw new CryptographicException(
                "Invalid Ed25519 public key: not a canonical point in the prime-order subgroup.");

        // Clear the sign bit; read the remaining 255 bits as a little-endian integer = y.
        var yBytes = (byte[])ed25519PublicKey.Clone();
        yBytes[31] &= 0x7F;
        var y = new BigInteger(yBytes, isUnsigned: true, isBigEndian: false) % P;

        var oneMinusY = Mod(BigInteger.One - y);
        if (oneMinusY.IsZero)
            throw new CryptographicException("Invalid Ed25519 public key: y == 1 has no Montgomery image.");

        var onePlusY = Mod(BigInteger.One + y);
        var u = Mod(onePlusY * BigInteger.ModPow(oneMinusY, P - 2, P)); // (1+y)/(1-y) mod p

        return ToLittleEndian32(u);
    }

    /// <summary>Salt for HKDF: the rotation id in canonical uppercase string form, UTF-8.</summary>
    private static byte[] SaltBytes(Guid rotationEventId)
        => Encoding.UTF8.GetBytes(rotationEventId.ToString().ToUpperInvariant());

    /// <summary>HKDF info: the version tag concatenated with the UPPERCASE node id, binding the wrap key to one node.</summary>
    private static byte[] InfoBytes(string upperNodeId)
    {
        var nodeBytes = Encoding.UTF8.GetBytes(upperNodeId);
        var info = new byte[InfoPrefix.Length + nodeBytes.Length];
        InfoPrefix.CopyTo(info, 0);
        nodeBytes.CopyTo(info, InfoPrefix.Length);
        return info;
    }

    // GUID-case trap: Guid.ToString() is lowercase but the DB and the AAD/peer-map use uppercase.
    // Uppercase everywhere so the initiator's peer keys and the receiver's lookup always match.
    private static string NodeKey(Guid nodeId) => nodeId.ToString().ToUpperInvariant();

    private static BigInteger Mod(BigInteger v)
    {
        var r = v % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static byte[] ToLittleEndian32(BigInteger v)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (raw.Length == CryptoConstants.KeySize)
            return raw;
        var padded = new byte[CryptoConstants.KeySize];
        Array.Copy(raw, padded, Math.Min(raw.Length, CryptoConstants.KeySize));
        return padded;
    }
}
