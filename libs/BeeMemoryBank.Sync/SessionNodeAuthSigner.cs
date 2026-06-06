using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Default <see cref="INodeAuthSigner"/>: signs using the node identity key derived via the
/// master DEK held by <see cref="SessionService"/>. This preserves the original sync behaviour
/// for server, CLI, and the unlocked mobile foreground. For legacy v=0 (plaintext) identity
/// rows the DEK is not needed; for v=1 rows it is fetched lazily (throws if the session is
/// locked — same as before this abstraction existed).
/// </summary>
public sealed class SessionNodeAuthSigner(SessionService session) : INodeAuthSigner
{
    public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) =>
        NodeIdentityCrypto.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey,
            identity.Ed25519PrivateKeyIV,
            identity.Ed25519PrivateKeyV,
            identity.NodeId,
            session.GetMasterDek,
            challengePayload);
}
