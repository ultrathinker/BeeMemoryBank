using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Default <see cref="INodeAuthSigner"/>: signs using the node identity key derived via the
/// master DEK held by <see cref="SessionService"/>. Used by server, CLI, and the unlocked mobile
/// foreground. For legacy v=0 (plaintext) identity rows the DEK is not needed; for v=1 rows it is
/// fetched lazily (throws if the session is locked). For v=2 rows (a blind node, which never has
/// the DEK) the seed comes from <paramref name="externalKey"/>, registered only in the blind role.
/// </summary>
public sealed class SessionNodeAuthSigner(SessionService session, IExternalNodeKey? externalKey = null) : INodeAuthSigner
{
    public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) =>
        NodeIdentityCrypto.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey,
            identity.Ed25519PrivateKeyIV,
            identity.Ed25519PrivateKeyV,
            identity.NodeId,
            session.GetMasterDek,
            externalKey is null ? null : externalKey.ReadSeed,
            challengePayload);
}
