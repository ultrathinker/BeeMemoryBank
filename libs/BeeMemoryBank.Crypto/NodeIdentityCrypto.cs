namespace BeeMemoryBank.Crypto;

/// <summary>
/// Crypto helpers for tbl_node_identity.ed25519_private_key:
///   v=0 = legacy plaintext seed (existing rows before migration 005)
///   v=1 = AES-GCM-wrapped seed under master DEK, AAD = "bmb-node-pk" || nodeId bytes
///   v=2 = the seed is not in the database at all (<see cref="ExternalKeyVersion"/>)
/// Use SignWithIdentity to sign — it decrypts on the fly and clears the plaintext seed.
/// </summary>
public static class NodeIdentityCrypto
{
    /// <summary>
    /// A node that never holds the master DEK — a blind node (plan 3.5) — cannot keep its signing
    /// key under it, yet has to authenticate to peers unattended. Its row keeps only the public
    /// key; the seed lives outside the database (a 0600 file in the data volume on Linux, the
    /// Android Keystore on a phone) and the caller supplies it. The private-key columns are empty.
    /// </summary>
    public const int ExternalKeyVersion = 2;

    private static readonly byte[] PrivateKeyAadPrefix = "bmb-node-pk"u8.ToArray();

    /// <summary>
    /// Returns the raw 32-byte Ed25519 seed for signing.
    /// For v=0 rows returns a defensive copy of the stored bytes; for v=1 rows decrypts.
    /// Caller is responsible for clearing the returned buffer.
    /// </summary>
    public static byte[] GetDecryptedPrivateKey(
        byte[] storedPrivateKey,
        byte[]? privateKeyIV,
        int privateKeyVersion,
        Guid nodeId,
        byte[] masterDek)
    {
        ArgumentNullException.ThrowIfNull(storedPrivateKey);
        ArgumentNullException.ThrowIfNull(masterDek);

        if (privateKeyVersion == 0)
        {
            var copy = new byte[storedPrivateKey.Length];
            storedPrivateKey.CopyTo(copy, 0);
            return copy;
        }

        // Explicit, rather than letting AES-GCM fail on an empty blob: every path that reaches here
        // with a v=2 row is one that should have gone to the external key instead, and the message
        // has to say so.
        if (privateKeyVersion == ExternalKeyVersion)
            throw new InvalidOperationException(
                "This node's identity key is kept outside the database (v=2); the database holds no copy to decrypt.");
        if (privateKeyVersion != 1)
            throw new InvalidOperationException($"Unknown node identity key version {privateKeyVersion}.");

        if (privateKeyIV is null)
            throw new InvalidOperationException("v1 node identity row missing ed25519_private_key_iv.");

        var aad = BuildPrivateKeyAad(nodeId);
        return MediaEncryptor.Decrypt(storedPrivateKey, privateKeyIV, masterDek, aad);
    }

    /// <summary>
    /// Encrypts a raw 32-byte Ed25519 seed with the master DEK for storage at v=1.
    /// Returns (wrappedBytes, iv).
    /// </summary>
    public static (byte[] wrapped, byte[] iv) EncryptPrivateKey(byte[] privateKey, byte[] masterDek, Guid nodeId)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(masterDek);
        var aad = BuildPrivateKeyAad(nodeId);
        return MediaEncryptor.Encrypt(privateKey, masterDek, aad);
    }

    /// <summary>
    /// Decrypts the node's private key, signs the payload, clears the plaintext seed.
    /// Use this instead of passing identity.Ed25519PrivateKey to Ed25519Signer.Sign directly: a v=1
    /// stored key is ciphertext.
    /// Caller still owns masterDek lifecycle.
    /// </summary>
    public static byte[] SignWithIdentity(
        byte[] storedPrivateKey,
        byte[]? privateKeyIV,
        int privateKeyVersion,
        Guid nodeId,
        byte[] masterDek,
        byte[] payload)
    {
        var pk = GetDecryptedPrivateKey(storedPrivateKey, privateKeyIV, privateKeyVersion, nodeId, masterDek);
        try
        {
            return Ed25519Signer.Sign(pk, payload);
        }
        finally
        {
            Array.Clear(pk);
        }
    }

    /// <summary>
    /// Convenience overload: for v=0 rows skip the masterDek (passes empty bytes); for v=1
    /// rows fetches masterDek lazily via the supplied callback. Caller never has to call
    /// SessionService.GetMasterDek() directly when the identity might be legacy.
    /// </summary>
    public static byte[] SignWithIdentityOrGetDek(
        byte[] storedPrivateKey,
        byte[]? privateKeyIV,
        int privateKeyVersion,
        Guid nodeId,
        Func<byte[]> getMasterDek,
        byte[] payload) =>
        SignWithIdentityOrGetDek(storedPrivateKey, privateKeyIV, privateKeyVersion, nodeId, getMasterDek,
            getExternalSeed: null, payload);

    /// <summary>
    /// Dispatches on the row version: v=0 plaintext, v=1 under the master DEK (fetched lazily),
    /// v=2 the seed from <paramref name="getExternalSeed"/> — never the DEK, which a v=2 node does
    /// not have. The seed is cleared after signing whichever way it was obtained.
    /// </summary>
    public static byte[] SignWithIdentityOrGetDek(
        byte[] storedPrivateKey,
        byte[]? privateKeyIV,
        int privateKeyVersion,
        Guid nodeId,
        Func<byte[]> getMasterDek,
        Func<byte[]>? getExternalSeed,
        byte[] payload)
    {
        if (privateKeyVersion == ExternalKeyVersion)
        {
            if (getExternalSeed is null)
                throw new InvalidOperationException(
                    "This node's identity key is kept outside the database (v=2), and no external key source is configured.");
            var seed = getExternalSeed();
            try
            {
                return Ed25519Signer.Sign(seed, payload);
            }
            finally
            {
                Array.Clear(seed);
            }
        }
        if (privateKeyVersion == 0)
        {
            return SignWithIdentity(storedPrivateKey, privateKeyIV, 0, nodeId, Array.Empty<byte>(), payload);
        }
        var dek = getMasterDek();
        try
        {
            return SignWithIdentity(storedPrivateKey, privateKeyIV, privateKeyVersion, nodeId, dek, payload);
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    /// <summary>The Ed25519 public key of a 32-byte seed.</summary>
    public static byte[] PublicKeyOf(byte[] seed) =>
        new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();

    private static byte[] BuildPrivateKeyAad(Guid nodeId)
    {
        var nodeBytes = nodeId.ToByteArray();
        var aad = new byte[PrivateKeyAadPrefix.Length + nodeBytes.Length];
        PrivateKeyAadPrefix.CopyTo(aad, 0);
        nodeBytes.CopyTo(aad, PrivateKeyAadPrefix.Length);
        return aad;
    }
}
