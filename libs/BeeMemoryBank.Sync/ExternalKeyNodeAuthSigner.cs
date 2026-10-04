using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The <see cref="INodeAuthSigner"/> of a node that never holds the master DEK (the Linux blind node): its identity is a v=2 row
/// and the seed comes from <see cref="IExternalNodeKey"/> (a 0600 file in the data volume). The full node's signer
/// (<c>SessionNodeAuthSigner</c>) also derives v=0/v=1 keys through the master DEK and is not part of a blind node.
/// </summary>
public sealed class ExternalKeyNodeAuthSigner(IExternalNodeKey externalKey) : INodeAuthSigner
{
    public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload)
    {
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException(
                $"This node signs only with an external identity key (v={NodeIdentityCrypto.ExternalKeyVersion}); the identity row has v={identity.Ed25519PrivateKeyV}.");
        return NodeIdentityCrypto.SignWithExternalSeed(externalKey.ReadSeed, challengePayload);
    }
}
