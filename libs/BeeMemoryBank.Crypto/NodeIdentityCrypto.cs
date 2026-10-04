namespace BeeMemoryBank.Crypto;

/// <summary>
/// The part of the node identity key (tbl_node_identity.ed25519_private_key) that every node shares, blind nodes included:
///   v=2 = the seed is not in the database at all (<see cref="ExternalKeyVersion"/>)
/// The master-DEK half (v=0 plaintext seed, v=1 seed wrapped under the master DEK) is <see cref="NodeIdentityVault"/>, which a
/// blind node does not contain.
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

    /// <summary>
    /// Signs <paramref name="payload"/> with the external (v=2) seed from <paramref name="getExternalSeed"/> — never the DEK,
    /// which a v=2 node does not have. The seed is cleared after signing.
    /// </summary>
    public static byte[] SignWithExternalSeed(Func<byte[]>? getExternalSeed, byte[] payload)
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

    /// <summary>The Ed25519 public key of a 32-byte seed.</summary>
    public static byte[] PublicKeyOf(byte[] seed) =>
        new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();
}
