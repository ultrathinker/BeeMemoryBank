using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Signs protocol-3 peer challenges with the phone's v=2 identity seed. The seed is loaded only
/// from Android Keystore and is cleared immediately after use; a blind phone never keeps it in
/// <c>tbl_node_identity</c> or derives it from a content DEK.
/// </summary>
public sealed class BlindKeystoreNodeAuthSigner(IBlindSecretStore keys) : INodeAuthSigner
{
    public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(challengePayload);
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
        {
            throw new InvalidOperationException(
                $"A blind phone signs only with an external v={NodeIdentityCrypto.ExternalKeyVersion} identity key.");
        }

        var seed = keys.LoadIdentitySeed()
            ?? throw new InvalidOperationException("The blind phone identity key is unavailable in Android Keystore.");
        try
        {
            return Ed25519Signer.Sign(seed, challengePayload);
        }
        finally
        {
            Array.Clear(seed);
        }
    }
}
