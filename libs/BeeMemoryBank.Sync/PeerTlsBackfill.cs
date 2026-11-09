using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The TLS key a device lacks for one of its peers, taken from a node it trusts (BMB-337). A blind node has a self-signed certificate and is trusted
/// only by the pin on its row (<see cref="SpkiPinRegistry"/>). A device that joined before 2.5.1 has that row without the pin: the join did not carry
/// it, and the <c>whitelist_add</c> of the blind node is older than the join, so no event will ever bring it. Such a device checked the blind node
/// through the public CAs and refused its certificate, while the hub reached it fine.
///
/// <para>The answer of <c>GET /api/sync/my-standing</c> says, for each peer of the responder, how the responder reaches it (<see cref="PeerProtocolSeen.TlsTrust"/>,
/// <see cref="PeerProtocolSeen.TlsSpki"/>: the same rule as the join, <see cref="JoinTls"/>). This device takes a key from it for a peer that has none, and
/// nothing else: pull only (it asks, it never sends one) and fill only (a row that has any mode or key is never changed, whatever the responder says).
/// Neither is a row whose TLS trust an event ever wrote: a <c>whitelist_update</c> that turned the setting off leaves the same empty columns as a row a
/// join wrote without them, with the concern dated by that event, and an off stays off. Only the undated state of migration 045 is filled (see
/// <see cref="IWhitelistRepository.FillMissingTlsTrustAsync"/>).</para>
///
/// <para>The responder has to be trusted. It is the node this session was verified against, never the id its answer claims; it must be an active
/// superadmin on this device's whitelist (the same word a whitelist event needs) and not a blind node. The offered value must be <c>pin</c> with a
/// well-formed key; <c>public-ca</c> is not taken (a row without a mode is checked through the public CAs already), and a damaged key is skipped.</para>
///
/// <para>The write is local bookkeeping (<see cref="IWhitelistRepository.FillMissingTlsTrustAsync"/>): no event, no version. The next session reads the
/// row again and holds the peer to the key (<see cref="PeerTlsAnchor.For"/>); the pin cache is dropped here, and the loop is told the peer changed so a
/// pause it earned without the key ends.</para>
/// </summary>
public sealed class PeerTlsBackfill(
    IWhitelistRepository whitelist,
    ILogger<PeerTlsBackfill> logger,
    SpkiPinRegistry? pins = null,
    IPeerStateResetter? peerState = null)
{
    /// <param name="verifiedResponder">The node this session was verified against (its identity and challenge): never read from the answer.</param>
    /// <param name="standing">What it answered.</param>
    /// <param name="self">This node: its own row is nobody's to fill.</param>
    /// <returns>How many peers got a key.</returns>
    public async Task<int> FillAsync(Guid verifiedResponder, MyStanding standing, Guid self)
    {
        ArgumentNullException.ThrowIfNull(standing);
        if (standing.Peers is not { Count: > 0 }) return 0;
        if (BlindNodeId.IsBlind(verifiedResponder)) return 0;
        if (await whitelist.GetByNodeIdAsync(verifiedResponder) is not { Status: WhitelistStatuses.Active, IsSuperadmin: true }) return 0;

        var filled = 0;
        foreach (var peer in standing.Peers)
        {
            // The responder does not list itself, and a peer's key is not taken from the peer's own word.
            if (peer is null || peer.NodeId == self || peer.NodeId == verifiedResponder) continue;
            // The join's rule: only a mode this build can act on survives, and a pin with a damaged key does not. Only a pin is taken.
            if (!JoinTls.TryInherit(peer.TlsTrust, peer.TlsSpki, out var trust, out var spki)
                || trust != BlindTrust.Pin || !Spki.IsWellFormed(spki)) continue;
            // The repository checks again, in the same statement as the write, that the row is still empty and never had its TLS trust written by an event
            // (an explicit "off" looks empty too), and that the responder is still an active superadmin: the check above is only the cheap way out.
            if (!await whitelist.FillMissingTlsTrustAsync(peer.NodeId, BlindTrust.Pin, spki!, verifiedResponder)) continue;

            filled++;
            // The key itself is not logged: the peer and the node that said it are enough to find the row.
            logger.LogInformation("This device had no TLS key for {PeerId}; it took the one {ResponderId} holds for that peer.", peer.NodeId, verifiedResponder);
            pins?.Invalidate();
            peerState?.PeerChanged(peer.NodeId);
        }
        return filled;
    }
}
