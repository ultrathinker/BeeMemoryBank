using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// Resolves the new master DEK that an applying node must swap to from a committed rotation, and the
/// node-local chain material to persist alongside it. Shared by all three apply paths — the server's
/// initiator Accept, the server's peer AutoAccept, and the mobile/CLI <see cref="PeerDekRotationApplier"/>
/// — so the "open my envelope, else fall back to the legacy wrap" decision lives in exactly one place.
/// </summary>
public static class DekRotationMaterial
{
    /// <summary>
    /// Produces the new master DEK for this node from a commit payload.
    ///
    /// <list type="bullet">
    ///   <item><b>Confidential rotation (ADR 0006).</b> <c>dek_envelopes</c> is present: look up this
    ///   node's envelope by its UPPERCASE node id, derive its X25519 private key from its Ed25519 seed
    ///   (v1 seeds are wrapped under <paramref name="oldDek"/>), and open the envelope. A node with no
    ///   envelope entry was added after the rotation and must get the DEK via join, not here — that is
    ///   a clear error rather than a silent fall-through.</item>
    ///   <item><b>Legacy rotation.</b> No envelopes but <c>encrypted_new_dek</c> is present (a rotation
    ///   event written before ADR 0006): unwrap it under <paramref name="oldDek"/>.</item>
    ///   <item><b>Neither.</b> Malformed — throw.</item>
    /// </list>
    ///
    /// <para><paramref name="chainEncryptedNewDekB64"/> / <paramref name="chainIvB64"/> return the
    /// node-local chain material for <c>LazySlotRewrapService</c>: the new DEK wrapped under the old
    /// DEK, exactly the shape the legacy <c>encrypted_new_dek</c> had. It is stored in
    /// <c>tbl_dek_rotation_state</c>, never synced, so it stays available after compaction removes the
    /// commit event — and for a confidential rotation it is computed here rather than taken off the
    /// wire, since the wire no longer carries a wrap-under-old-DEK copy.</para>
    /// </summary>
    public static byte[] ResolveNewDek(
        DekRotationCommitPayload payload,
        Guid commitEventId,
        NodeIdentity identity,
        byte[] oldDek,
        out string chainEncryptedNewDekB64,
        out string chainIvB64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(oldDek);

        if (payload.DekEnvelopes is { } envelopes)
        {
            var nodeKey = identity.NodeId.ToString().ToUpperInvariant();
            if (!envelopes.Peers.TryGetValue(nodeKey, out var box))
                throw new InvalidOperationException(
                    $"DEK rotation commit {commitEventId} carries no envelope for this node ({nodeKey}). " +
                    "A node added after the rotation receives the current DEK through join, not through " +
                    "this event.");

            // v1 identity seeds are wrapped under the CURRENT master DEK, which is oldDek here (the
            // swap has not happened yet).
            var seed = NodeIdentityCrypto.GetDecryptedPrivateKey(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, oldDek);

            byte[] newDek;
            try
            {
                newDek = DekEnvelope.Open(
                    envelopes.EphemeralPub, box.Wrapped, box.Nonce,
                    commitEventId, identity.NodeId, seed);
            }
            finally
            {
                Array.Clear(seed);
            }

            var (chainEnc, chainIv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
            chainEncryptedNewDekB64 = Convert.ToBase64String(chainEnc);
            chainIvB64 = Convert.ToBase64String(chainIv);
            return newDek;
        }

        if (payload.EncryptedNewDek is { } encB64 && payload.Iv is { } ivB64)
        {
            var encNewDekBytes = Convert.FromBase64String(encB64);
            var ivBytes = Convert.FromBase64String(ivB64);
            try
            {
                var newDek = MasterKeyManager.UnwrapMasterDek(encNewDekBytes, ivBytes, oldDek);
                chainEncryptedNewDekB64 = encB64;
                chainIvB64 = ivB64;
                return newDek;
            }
            finally
            {
                Array.Clear(encNewDekBytes);
            }
        }

        throw new InvalidOperationException(
            $"DEK rotation commit {commitEventId} carries neither dek_envelopes nor encrypted_new_dek.");
    }
}
