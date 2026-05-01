namespace BeeMemoryBank.Crypto;

/// <summary>
/// Crypto helpers for tbl_node_identity.ed25519_private_key:
///   v=0 = legacy plaintext seed (existing rows before migration 005)
///   v=1 = AES-GCM-wrapped seed under master DEK, AAD = "bmb-node-pk" || nodeId bytes
/// Use SignWithIdentity to sign — it decrypts on the fly and clears the plaintext seed.
/// </summary>
public static class NodeIdentityCrypto
{
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
    /// Use this in callsites that previously did Ed25519Signer.Sign(identity.Ed25519PrivateKey, ...).
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
        byte[] payload)
    {
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

    private static byte[] BuildPrivateKeyAad(Guid nodeId)
    {
        var nodeBytes = nodeId.ToByteArray();
        var aad = new byte[PrivateKeyAadPrefix.Length + nodeBytes.Length];
        PrivateKeyAadPrefix.CopyTo(aad, 0);
        nodeBytes.CopyTo(aad, PrivateKeyAadPrefix.Length);
        return aad;
    }
}
