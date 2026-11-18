using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// A sealed door (door format v2, docs/adr/0012-sealed-doors.md): the master DEK sealed to an X25519 public key that only the door's
/// secret can re-derive.
/// <code>
/// door_sk = clamp(HKDF-SHA256(ikm = KEK, salt = door salt or empty, info = "bmb-door-x25519-v1"));   door_pk = X25519(door_sk, 9)
/// seal    : eph pair; z = X25519(eph_sk, door_pk) (all-zero refused);
///           key = HKDF-SHA256(ikm = z, salt = door_pk || eph_pk, info = "bmb-door-v2\0" || kind)
///           box = 0x02 || eph_pk(32) || nonce(12) || AES-256-GCM(key, dek, aad = 0x02 || door_pk || eph_pk)       -- 93 bytes
/// open    : KEK -> door_sk -> z = X25519(door_sk, eph_pk) -> key -> AES-GCM
/// </code>
/// The KEK is whatever the door's secret derived before (Argon2id of a password or a recovery key, the auto-unlock secret, the agent key's
/// HKDF). Unlike a wrap under the KEK, a box can be sealed again to the same door from its public key alone, without the secret; that is
/// what lets a rotation re-seal the doors of people who are not there. Every intermediate key buffer is zeroed before return.
/// </summary>
public static class DoorBox
{
    public const byte Version = 0x02;
    public const int PublicKeySize = 32;
    public const int SecretKeySize = 32;
    public const int NonceSize = 12;
    public const int BoxSize = 1 + PublicKeySize + NonceSize + CryptoConstants.KeySize + CryptoConstants.TagSize; // 93

    /// <summary>Tests only: when set on a thread, every intermediate key buffer of that thread's calls is added, so a test can see it zeroed.</summary>
    [ThreadStatic] internal static List<byte[]>? TrackedIntermediates;

    private static readonly byte[] KeyPairInfo = "bmb-door-x25519-v1"u8.ToArray();
    private static readonly byte[] BoxInfoPrefix = "bmb-door-v2\0"u8.ToArray();

    /// <summary>A door's key pair. <see cref="Dispose"/> zeroes the secret.</summary>
    public sealed class KeyPair : IDisposable
    {
        internal KeyPair(byte[] secretKey, byte[] publicKey)
        {
            SecretKey = secretKey;
            PublicKey = publicKey;
        }

        public byte[] SecretKey { get; }
        public byte[] PublicKey { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(SecretKey);
    }

    /// <summary>The door key pair of a KEK and the door's salt (an empty or null salt is HKDF's empty salt).</summary>
    public static KeyPair DeriveKeyPair(byte[] kek, byte[]? salt)
    {
        ArgumentNullException.ThrowIfNull(kek);
        if (kek.Length == 0) throw new ArgumentException("A door needs a key.", nameof(kek));
        var secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, kek, SecretKeySize, salt ?? [], KeyPairInfo);
        Clamp(secret);
        var pub = new byte[PublicKeySize];
        X25519.ScalarMultBase(secret, 0, pub, 0);
        return new KeyPair(secret, pub);
    }

    /// <summary>The public key of a door secret.</summary>
    public static byte[] PublicKeyOf(byte[] doorSecretKey)
    {
        ArgumentNullException.ThrowIfNull(doorSecretKey);
        if (doorSecretKey.Length != SecretKeySize) throw new ArgumentException("A door secret is 32 bytes.", nameof(doorSecretKey));
        var pub = new byte[PublicKeySize];
        X25519.ScalarMultBase(doorSecretKey, 0, pub, 0);
        return pub;
    }

    /// <summary>Seals <paramref name="dek"/> to the door <paramref name="doorPublicKey"/> of kind <paramref name="kind"/>; a fresh ephemeral key and nonce.</summary>
    /// <exception cref="CryptographicException">The public key is a low-order point (the shared secret would be all zero).</exception>
    public static byte[] Seal(byte[] doorPublicKey, string kind, byte[] dek)
    {
        var ephemeral = Track(SecureRandom.GetBytes(SecretKeySize));
        var nonce = SecureRandom.GetBytes(NonceSize);
        try
        {
            return Seal(doorPublicKey, kind, dek, ephemeral, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeral);
        }
    }

    /// <summary>The deterministic form of <see cref="Seal(byte[], string, byte[])"/>, for the fixed test vectors.</summary>
    internal static byte[] Seal(byte[] doorPublicKey, string kind, byte[] dek, byte[] ephemeralSecret, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(doorPublicKey);
        ArgumentNullException.ThrowIfNull(dek);
        ArgumentNullException.ThrowIfNull(ephemeralSecret);
        ArgumentNullException.ThrowIfNull(nonce);
        if (doorPublicKey.Length != PublicKeySize) throw new ArgumentException("A door public key is 32 bytes.", nameof(doorPublicKey));
        if (dek.Length != CryptoConstants.KeySize) throw new ArgumentException("A master DEK is 32 bytes.", nameof(dek));
        if (ephemeralSecret.Length != SecretKeySize) throw new ArgumentException("An ephemeral secret is 32 bytes.", nameof(ephemeralSecret));
        if (nonce.Length != NonceSize) throw new ArgumentException("A nonce is 12 bytes.", nameof(nonce));
        var info = InfoOf(kind);

        var eph = Track((byte[])ephemeralSecret.Clone());
        var shared = Track(new byte[32]);
        byte[]? key = null;
        try
        {
            Clamp(eph);
            var ephPublic = new byte[PublicKeySize];
            X25519.ScalarMultBase(eph, 0, ephPublic, 0);
            if (!X25519.CalculateAgreement(eph, 0, doorPublicKey, 0, shared, 0))
                throw new CryptographicException("The door public key is not usable (a low-order point).");

            key = Track(HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, CryptoConstants.KeySize, Concat(doorPublicKey, ephPublic), info));
            var box = new byte[BoxSize];
            box[0] = Version;
            ephPublic.CopyTo(box, 1);
            nonce.CopyTo(box, 1 + PublicKeySize);
            using var aes = new AesGcm(key, CryptoConstants.TagSize);
            aes.Encrypt(nonce, dek,
                box.AsSpan(1 + PublicKeySize + NonceSize, CryptoConstants.KeySize),
                box.AsSpan(1 + PublicKeySize + NonceSize + CryptoConstants.KeySize, CryptoConstants.TagSize),
                Aad(doorPublicKey, ephPublic));
            return box;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eph);
            CryptographicOperations.ZeroMemory(shared);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>True when <paramref name="box"/> has the shape of a v2 box (length and version byte); says nothing about who can open it.</summary>
    public static bool IsWellFormed(ReadOnlySpan<byte> box) => box.Length == BoxSize && box[0] == Version;

    /// <summary>
    /// Opens a box with the door secret. Null for a malformed box (length, version byte), a low-order ephemeral key, another kind, another door
    /// or any change of a byte. The caller owns and clears the returned DEK.
    /// </summary>
    public static byte[]? TryOpen(byte[] doorSecretKey, string kind, byte[] box)
    {
        ArgumentNullException.ThrowIfNull(doorSecretKey);
        ArgumentNullException.ThrowIfNull(box);
        if (doorSecretKey.Length != SecretKeySize || !IsWellFormed(box)) return null;
        var info = InfoOf(kind);

        var ephPublic = box.AsSpan(1, PublicKeySize).ToArray();
        var doorPublic = PublicKeyOf(doorSecretKey);
        var shared = Track(new byte[32]);
        byte[]? key = null;
        var dek = new byte[CryptoConstants.KeySize];
        try
        {
            if (!X25519.CalculateAgreement(doorSecretKey, 0, ephPublic, 0, shared, 0)) return null;
            key = Track(HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, CryptoConstants.KeySize, Concat(doorPublic, ephPublic), info));
            using var aes = new AesGcm(key, CryptoConstants.TagSize);
            aes.Decrypt(box.AsSpan(1 + PublicKeySize, NonceSize),
                box.AsSpan(1 + PublicKeySize + NonceSize, CryptoConstants.KeySize),
                box.AsSpan(1 + PublicKeySize + NonceSize + CryptoConstants.KeySize, CryptoConstants.TagSize),
                dek, Aad(doorPublic, ephPublic));
            return dek;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(dek);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Derives the door secret from the KEK and opens the box; null on any failure. The caller clears the DEK.</summary>
    public static byte[]? TryOpenWithKek(byte[] kek, byte[]? salt, string kind, byte[] box)
    {
        using var pair = DeriveKeyPair(kek, salt);
        return TryOpen(pair.SecretKey, kind, box);
    }

    private static byte[] InfoOf(string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        foreach (var c in kind)
            if (c is < '!' or > '~') throw new ArgumentException("A door kind is printable ASCII.", nameof(kind));
        var kindBytes = Encoding.ASCII.GetBytes(kind);
        return Concat(BoxInfoPrefix, kindBytes);
    }

    private static byte[] Aad(byte[] doorPublic, byte[] ephPublic)
    {
        var aad = new byte[1 + PublicKeySize + PublicKeySize];
        aad[0] = Version;
        doorPublic.CopyTo(aad, 1);
        ephPublic.CopyTo(aad, 1 + PublicKeySize);
        return aad;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static byte[] Track(byte[] buffer)
    {
        TrackedIntermediates?.Add(buffer);
        return buffer;
    }

    private static void Clamp(byte[] scalar)
    {
        scalar[0] &= 248;
        scalar[31] &= 127;
        scalar[31] |= 64;
    }
}
