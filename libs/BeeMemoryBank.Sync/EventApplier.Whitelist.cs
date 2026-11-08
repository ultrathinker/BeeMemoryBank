using System.Globalization;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    // The whitelist converges whatever order its events arrive in. A peer's row has five concerns (WhitelistConcerns: membership, superadmin,
    // name, address, TLS trust), each with the version of the newest event that wrote it (migration 045), and each is last-writer-wins by
    // (Lamport time, node) on its own (ConflictResolver):
    //  - a whitelist_add writes all five (membership: active), a whitelist_revoke membership only (revoked), a whitelist_update the fields it
    //    names (WhitelistEventConcerns). So a revoke is never undone by an update, which cannot write membership, whatever its version, and a
    //    re-add newer than the revoke still wins; two updates of different fields that crossed both stand (a rename does not drop a demotion).
    //  - a revoke that finds no row leaves a revoked tombstone at its version (CreateTombstoneAsync), its fields unversioned until an event
    //    names them; an update applies to a revoked row as to an active one (its fields wait there for a re-add, judged per field).
    //  - an update that finds no row stays in the log, and the event that writes the row applies it after itself (ReplayUpdatesAsync).
    // Two exceptions, both decided by the events alone, never by the order they arrive in:
    //  - a row revoked before versioning (membership version 0) refuses every remote add (see the add below);
    //  - an undated field (WhitelistEntry.UndatedFields: written by a join from a 2.7.5 host, a restore, a blind package or an older build,
    //    whose writer this node cannot name; its version stays the real one) is changed by an update newer than its version, which dates it,
    //    and by a re-add (an add newer than a revoke of the peer that is itself newer than the field's value: its version, or for a value at
    //    version 0 the last join or restore of this node), but not by any other add: what the source said cannot be dated against an add
    //    delivered late from an old log, as an older build never let an add change an active row. The adds of the log are run again when a
    //    revoke or a dating update arrives (ReplayAddsAsync), so an add that came before the event that made it count is not lost.
    // The one-off repair (RepairWhitelistOrderAsync) runs the log through these same rules, and dates a field of a row written before 045 (or
    // gives a concern the version of its own last write) where the log shows which event that was.

    private static readonly WhitelistConcerns[] FieldConcerns =
        [WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls];

    /// <param name="knownRevoke">A revoke being applied right now (not in the log yet) that the replay of the adds after it hands over.</param>
    private async Task ApplyWhitelistAddAsync(SyncEvent evt, bool repairing = false, RowVersion? knownRevoke = null)
    {
        var p = Deserialize<WhitelistAddPayload>(evt.Payload);

        // Skip self — a node must never be in its own whitelist.
        // This can happen when another node adds *us* as their trusted node and
        // syncs that whitelist_add event back to us. We already know we exist
        // (tbl_node_identity); having a row in tbl_whitelist about ourselves
        // is confusing in the UI and corrupts sync position bookkeeping.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        var now = DateTime.UtcNow;
        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null)
        {
            // The repair never brings a peer into being (RepairWhitelistOrderAsync does not even ask).
            if (repairing) return;

            var created = new WhitelistEntry
            {
                NodeId = p.NodeId,
                Ed25519PublicKey = Convert.FromBase64String(p.PublicKeyB64),
                Status = WhitelistStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ApplyAddFields(created, p, AddressFromEvent(p.NodeId, p.ApiAddress));
            created.SetAllVersions(incoming);
            await whitelistRepoWrite.CreateAsync(created);
            await ReplayUpdatesAsync(p.NodeId);
            return;
        }

        // A row a restore kept unconfirmed is not touched by any add or update until a superadmin confirms it (RestoredPeerStatus): it must not
        // become a superadmin, or move, on events no anchor vouched for. A revoke still applies to it.
        if (existing.Status is not (WhitelistStatuses.Active or WhitelistStatuses.Revoked))
        {
            LogWhitelistDrop(repairing, EventTypes.WhitelistAdd, p.NodeId, existing.VersionOf(WhitelistConcerns.Membership), incoming);
            return;
        }

        // The re-activation below must not run on ANY add for a revoked peer regardless of when
        // that add was issued: an admin revokes a compromised node, a peer that was offline at
        // the time still holds the older whitelist_add for it and delivers it on catch-up, and
        // the revoked node is back in the mesh with nothing to distinguish "my revoke was
        // undone" from "my revoke never applied".
        //
        // Deliberately plain LWW rather than a special "revoke always wins" rule: re-adding a
        // peer you previously revoked is a real workflow the UI offers, so revoke has to be
        // undoable — by a NEWER add. That is exactly what the comparison of membership allows.
        // A revoked row that predates versioning cannot be compared, and must not lose by
        // default. Rows revoked before migration 021 sit at Lamport 0, so ANY incoming add
        // outranks them arithmetically — which would let a stale add from a peer that never
        // heard about the revocation put a revoked, possibly compromised, node back into the
        // mesh. Nothing in the row says whether the revoke was newer, so the safe reading of
        // "unknown" is that it was.
        //
        // This is not a dead end for the admin who genuinely wants the node back: adding a peer
        // locally writes the row directly with a fresh version, and that path is untouched. Only
        // a REMOTE add against an unversioned revocation is refused — which is exactly the
        // shape wanted, since a local admin action is a decision and an arriving old event is
        // not.
        if (existing.Status == WhitelistStatuses.Revoked && existing.MemberLamportTs == 0)
        {
            logger.LogWarning(
                "WhitelistAdd for {NodeId} refused: the local row is revoked and predates row versioning, "
                + "so the revoke cannot be compared against this event. Re-add the node locally if it should return.",
                p.NodeId);
            return;
        }

        var wasActive = existing.Status == WhitelistStatuses.Active;
        var memberBefore = existing.VersionOf(WhitelistConcerns.Membership);
        var placeBefore = Place(existing);
        var changed = false;

        // The key is bound to the node id at first registration, and NEVER replaced: a stale or replayed add with another key would allow
        // impersonation with a compromised old key. A tombstone has none yet: the first add that names the node binds it, older than the revoke
        // or newer, so a later re-add is held to that key as it is on a node that had the row before the revoke.
        if (existing.Ed25519PublicKey.Length == 0)
        {
            existing.Ed25519PublicKey = Convert.FromBase64String(p.PublicKeyB64);
            changed = true;
        }

        // Membership: newer than the revoke (or the add) the row stands on, it is active at this add's version. An add older than a revoke is
        // refused, in either order of arrival; a revoke older than this add, arriving later, loses to it (the peer was revoked and added again
        // elsewhere, and this node has not seen the revoke yet).
        if (ConflictResolver.IncomingWins(existing.VersionOf(WhitelistConcerns.Membership), incoming))
        {
            existing.SetVersionOf(WhitelistConcerns.Membership, incoming);
            if (!wasActive)
            {
                existing.Status = WhitelistStatuses.Active;
                // As when an add makes the row: can_generate_embeddings is this node's own afterwards (no update carries it).
                existing.CanGenerateEmbeddings = p.CanGenerateEmbeddings;
            }
            changed = true;
        }

        // The fields: each one this add is newer than takes the add's value; an undated one only from a re-add (see the top).
        var stated = new WhitelistEntry { NodeId = p.NodeId };
        ApplyAddFields(stated, p, AddressFromEvent(p.NodeId, p.ApiAddress, warn: !repairing));
        var revokes = new ReAddEvidence(this, p.NodeId, wasActive ? null : memberBefore, knownRevoke);
        foreach (var concern in FieldConcerns)
        {
            if (!ConflictResolver.IncomingWins(existing.VersionOf(concern), incoming)) continue;
            if (existing.IsUndated(concern) && !await revokes.IsReAddAsync(existing.VersionOf(concern), incoming)) continue;
            CopyValue(concern, stated, existing);
            existing.SetVersionOf(concern, incoming);
            changed = true;
        }

        if (!changed)
            LogWhitelistDrop(repairing, EventTypes.WhitelistAdd, p.NodeId, existing.VersionOf(WhitelistConcerns.Membership), incoming);
        else
        {
            existing.UpdatedAt = now;
            SetRowVersionToNewestConcern(existing);
            await whitelistRepoWrite.UpdateAsync(existing);
            // Back in the network, or at another place: what this node remembered of the peer does not count.
            var active = existing.Status == WhitelistStatuses.Active;
            if (active && (!wasActive || Place(existing) != placeBefore)) peerState?.PeerChanged(p.NodeId);
        }

        // The updates that arrived before there was a row are applied now; each is judged per field as on arrival, so taking them again (an add
        // delivered once more after a crash between the row and the log) changes nothing. The repair takes the log in version order itself.
        if (!repairing) await ReplayUpdatesAsync(p.NodeId);
    }

    private async Task ApplyWhitelistRevokeAsync(SyncEvent evt, bool repairing = false)
    {
        var p = Deserialize<WhitelistRevokePayload>(evt.Payload);

        // Never revoke self via a remote event — we can't revoke ourselves.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null)
        {
            // The revoke came before the add (a new node pulls the history from several peers, each in its own order). Without a row at the revoke's
            // version the add that comes next finds nothing to lose to and makes an active row, and this revoke, logged as applied, never comes
            // again: the node would trust a peer the rest of the network revoked, and hand it on to every node that joins through it. Comment
            // deletes leave a placeholder for the same reason (ApplyCommentDeleteAsync).
            if (await whitelistRepoWrite.CreateTombstoneAsync(p.NodeId, incoming))
            {
                logger.LogInformation("WhitelistRevoke for {NodeId} arrived before its add: kept as a revoked row at ({EventTs}, {EventNode})",
                    p.NodeId, incoming.LamportTs, incoming.SourceNodeId);
                // The updates that came before any row write its fields now, so a re-add is judged against them field by field.
                if (!repairing) await ReplayUpdatesAsync(p.NodeId);
                return;
            }
            // A row was written in between (a local add does not wait for applies): judge the revoke against it, as below.
            existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
            if (existing == null)
            {
                logger.LogWarning("WhitelistRevoke for {NodeId}: no row could be written for it, and none is there", p.NodeId);
                return;
            }
        }

        // A row revoked before versioning (membership version 0) stays as it is: it refuses every remote add, and a version would make a newer add
        // able to undo a revocation nothing here can date (see the add above).
        if (existing.Status == WhitelistStatuses.Revoked && existing.MemberLamportTs == 0) return;

        // Judged against membership only: an add newer than this revoke is the newer decision, and re-revoking on its arrival would undo it; an
        // update, whatever its version, says nothing about membership. A revoked row takes a newer revoke's version, so an add older than THIS
        // revoke (and newer than the first) is refused too. Applies to an unconfirmed row as well: a revocation never needs a vouching anchor.
        if (!ConflictResolver.IncomingWins(existing.VersionOf(WhitelistConcerns.Membership), incoming))
        {
            if (existing.Status != WhitelistStatuses.Revoked)
                LogWhitelistDrop(repairing, EventTypes.WhitelistRevoke, p.NodeId, existing.VersionOf(WhitelistConcerns.Membership), incoming);
        }
        else
        {
            var revokedNow = existing.Status != WhitelistStatuses.Revoked;
            existing.Status = WhitelistStatuses.Revoked;
            existing.SetVersionOf(WhitelistConcerns.Membership, incoming);
            if (revokedNow)
            {
                var now = DateTime.UtcNow;
                existing.DeletedAt = now;
                existing.UpdatedAt = now;
            }
            SetRowVersionToNewestConcern(existing);
            await whitelistRepoWrite.UpdateAsync(existing);
            if (revokedNow) peerState?.PeerChanged(p.NodeId);
        }

        // Whether it won or lost, an add newer than this revoke is a re-add from now on: one that came before it may write the undated fields
        // now (this revoke is not in the log yet, so it is handed over). The repair takes the log in version order itself.
        if (!repairing && FieldConcerns.Any(existing.IsUndated)) await ReplayAddsAsync(p.NodeId, incoming, knownRevoke: incoming);
    }

    private Task ApplyWhitelistUpdateAsync(SyncEvent evt, bool repairing = false) => ApplyUpdateFieldsAsync(evt, quiet: repairing);

    /// <summary>
    /// A <c>whitelist_update</c>, field by field: each field it names that this node takes, and is newer than the version that field has, takes
    /// its value. An active row and a revoked one alike (membership is not a field an update can write). With no row (its add has not arrived)
    /// it stays in the log, and the event that writes the row applies it after itself (<see cref="ReplayUpdatesAsync"/>); an unconfirmed row
    /// (a restore's) takes none until it is confirmed (see the add above).
    /// </summary>
    /// <param name="quiet">The repair or a replay: what loses is logged at debug level only.</param>
    /// <returns>True when the row changed.</returns>
    private async Task<bool> ApplyUpdateFieldsAsync(SyncEvent evt, bool quiet)
    {
        var p = Deserialize<WhitelistUpdatePayload>(evt.Payload);

        // Never update self via a remote event.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return false;

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status is not (WhitelistStatuses.Active or WhitelistStatuses.Revoked)) return false;

        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        var address = AddressFromEvent(p.NodeId, p.ApiAddress);
        var carried = WhitelistEventConcerns.OfUpdate(p, evt.NodeId, address, out var refusal);
        if (!quiet)
        {
            switch (refusal)
            {
                case WhitelistEventConcerns.Refusal.BlindSuperadmin:
                    logger.LogWarning(
                        "Ignoring whitelist_update from {Sender} raising blind node {NodeId} to superadmin: a blind node never is one",
                        evt.NodeId, p.NodeId);
                    break;
                case WhitelistEventConcerns.Refusal.SelfPromotion:
                    // A node may not promote itself. In practice the superadmin gate in ApplyAsync gets there first (a demoted peer is no longer
                    // superadmin here, so its whitelist_update is refused outright), and no node ever has its own row to send this from. Written
                    // down all the same: the gate asks "may this node change cluster state at all", and if it is ever relaxed (an "ordinary peers
                    // may rename themselves" feature is the obvious way), regaining authority must not come with it. Refused whatever the row says,
                    // so the answer does not depend on what arrived first. Demoting itself stays allowed: giving up a privilege needs no protection.
                    logger.LogWarning(
                        "Ignoring self-promotion: node {NodeId} sent a whitelist_update raising its own is_superadmin",
                        evt.NodeId);
                    break;
            }
        }

        // Without a version per field, two admins renaming the same peer (or moving its address) would resolve to whichever event happened to
        // arrive last, and a rename that crossed a demotion would drop it on the nodes that saw the rename first.
        var won = WhitelistConcerns.None;
        foreach (var concern in FieldConcerns)
            if (carried.HasFlag(concern) && ConflictResolver.IncomingWins(existing.VersionOf(concern), incoming))
                won |= concern;
        if (won == WhitelistConcerns.None)
        {
            if (carried != WhitelistConcerns.None)
                LogWhitelistDrop(quiet, EventTypes.WhitelistUpdate, p.NodeId, existing.Version, incoming);
            // This update again (delivered once more after a crash between the row and the log, ApplyViewAsync writing the row first): the adds it
            // was to be followed by may not have been taken yet.
            if (!quiet && FieldConcerns.Any(c => carried.HasFlag(c) && existing.VersionOf(c) == incoming)) await ReplayAddsAsync(p.NodeId, incoming);
            return false;
        }

        var placeBefore = Place(existing);
        var dated = FieldConcerns.Where(c => won.HasFlag(c) && existing.IsUndated(c)).Any();
        if (won.HasFlag(WhitelistConcerns.Address)) existing.ApiAddress = address;
        if (won.HasFlag(WhitelistConcerns.Name)) existing.DisplayName = p.DisplayName!;
        if (won.HasFlag(WhitelistConcerns.Tls)) ApplyTlsTrust(existing, p.TlsTrust, p.TlsSpki, isAdd: false);
        if (won.HasFlag(WhitelistConcerns.Superadmin)) existing.IsSuperadmin = p.IsSuperadmin!.Value;
        existing.SetVersionOf(won, incoming);
        existing.UpdatedAt = DateTime.UtcNow;
        SetRowVersionToNewestConcern(existing);
        await whitelistRepoWrite.UpdateAsync(existing);
        // Another address is another place: what was learned by calling the old one does not count for it, and the new one is tried at once.
        if (existing.Status == WhitelistStatuses.Active && Place(existing) != placeBefore) peerState?.PeerChanged(p.NodeId);
        // A field this update dated is judged by version from now on: an add newer than this update that came first writes it now. (The repair
        // and the replays take the log in version order themselves.)
        if (dated && !quiet) await ReplayAddsAsync(p.NodeId, incoming);
        return true;
    }

    /// <summary>
    /// The revokes that can make an add a re-add for an undated field: one in the log, the one the row stood revoked at when the add came, or
    /// the one being applied right now (not in the log yet). Only a revoke newer than the field's value counts: its version, or for a value at
    /// version 0 the last join or restore of this node (<see cref="LamportFloor"/>), after which the value was what the host or the backup said.
    /// A revoke from before that is history the value already reflects. Decided by the events: a revoke that arrives after the add runs the adds
    /// again (<see cref="ReplayAddsAsync"/>). Loads the log once per add.
    /// </summary>
    private sealed class ReAddEvidence(EventApplier applier, Guid nodeId, RowVersion? revokedAt, RowVersion? knownRevoke)
    {
        private List<RowVersion>? _revokes;
        private RowVersion? _floor;

        public async Task<bool> IsReAddAsync(RowVersion field, RowVersion add)
        {
            var since = field.LamportTs > 0 ? field : _floor ??= applier.Floor();
            bool Counts(RowVersion revoke) => ConflictResolver.IncomingWins(since, revoke) && ConflictResolver.IncomingWins(revoke, add);
            if (revokedAt is { } revoked && Counts(revoked)) return true;
            if (knownRevoke is { } now && Counts(now)) return true;
            _revokes ??= await applier.LoggedRevokesAsync(nodeId);
            return _revokes.Any(Counts);
        }
    }

    /// <summary>The versions of the revokes of <paramref name="nodeId"/> in the log.</summary>
    private async Task<List<RowVersion>> LoggedRevokesAsync(Guid nodeId) =>
        (await WhitelistHistory.ForPeerAsync(connFactory, nodeId)).Where(f => f.EventType == EventTypes.WhitelistRevoke).Select(f => f.Version).ToList();

    /// <summary>The Lamport time of this node's last join or recovery restore (0 for none), as a version no event ties with.</summary>
    private RowVersion Floor()
    {
        using var conn = connFactory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        return new RowVersion(LamportFloor.Read(conn), Guid.Empty);
    }

    /// <summary>
    /// The adds of <paramref name="nodeId"/> in the log newer than <paramref name="after"/>, through the rules again, when the row has an undated
    /// field: a revoke that just arrived makes an add newer than it a re-add, and an update that just dated a field lets an add newer than it
    /// write that field, as each would have on arrival had the other come first. Judged per field as always, so taking an add again changes nothing.
    /// </summary>
    private async Task ReplayAddsAsync(Guid nodeId, RowVersion after, RowVersion? knownRevoke = null)
    {
        foreach (var add in await WhitelistHistory.ForPeerAsync(connFactory, nodeId))
        {
            if (add.EventType != EventTypes.WhitelistAdd || !ConflictResolver.IncomingWins(after, add.Version)) continue;
            try { await ApplyWhitelistAddAsync(add.ToEvent(), repairing: true, knownRevoke); }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                logger.LogWarning(ex, "WhitelistAdd {EventId} for {NodeId} in the log does not read; not taken again", add.EventId, nodeId);
            }
        }
    }

    /// <summary>
    /// The updates of <paramref name="nodeId"/> in the log, in version order, through the rules again: those that arrived while there was no row
    /// apply now as they would have after the row was written. Each is judged per field, so taking one again changes nothing.
    /// </summary>
    private async Task ReplayUpdatesAsync(Guid nodeId)
    {
        foreach (var update in await WhitelistHistory.ForPeerAsync(connFactory, nodeId))
        {
            if (update.EventType != EventTypes.WhitelistUpdate) continue;
            if (await ApplyUpdateFieldsAsync(update.ToEvent(), quiet: true))
                logger.LogInformation("WhitelistUpdate {EventId} for {NodeId} arrived before the row it changes; applied now", update.EventId, nodeId);
        }
    }

    /// <summary>The row's own version: the newest version of its concerns (a join raises its clock past it).</summary>
    private static void SetRowVersionToNewestConcern(WhitelistEntry row)
    {
        var newest = row.VersionOf(WhitelistConcerns.Membership);
        foreach (var concern in WhitelistConcernSet.Each)
            if (ConflictResolver.IncomingWins(newest, row.VersionOf(concern))) newest = row.VersionOf(concern);
        row.SetRowVersion(newest);
    }

    private static bool SameValue(WhitelistConcerns concern, WhitelistEntry a, WhitelistEntry b) => concern switch
    {
        WhitelistConcerns.Superadmin => a.IsSuperadmin == b.IsSuperadmin,
        WhitelistConcerns.Name => a.DisplayName == b.DisplayName,
        WhitelistConcerns.Address => a.ApiAddress == b.ApiAddress,
        WhitelistConcerns.Tls => a.TlsSpki == b.TlsSpki && a.TlsTrust == b.TlsTrust,
        _ => false,
    };

    private static void CopyValue(WhitelistConcerns concern, WhitelistEntry from, WhitelistEntry to) => WhitelistEventConcerns.CopyField(concern, from, to);

    /// <summary>Where a peer is called and how its TLS endpoint is trusted: when it changes, the loop forgets what it learned about the peer.</summary>
    private static (string?, string?, string?) Place(WhitelistEntry row) => (PeerContactLog.EndpointKey(row.ApiAddress), row.TlsTrust, row.TlsSpki);

    /// <summary>
    /// Everything an add says about the peer but its key and its membership, onto a row (the key is bound to the node id once, by the caller).
    /// <paramref name="address"/> has been through the address policy.
    /// </summary>
    private static void ApplyAddFields(WhitelistEntry row, WhitelistAddPayload p, string? address)
    {
        row.DisplayName = p.DisplayName;
        row.ApiAddress = address;
        row.CanGenerateEmbeddings = p.CanGenerateEmbeddings;
        // A blind node is never a superadmin, whatever the event says (plan 3.2, BMB-42): it holds no
        // DEK, and a peer that marked it so — by mistake or on purpose — must not hand it authority here.
        row.IsSuperadmin = p.IsSuperadmin && !BlindNodeId.IsBlind(p.NodeId);
        ApplyTlsTrust(row, p.TlsTrust, p.TlsSpki, isAdd: true);
    }

    /// <summary>One row the repair changed.</summary>
    /// <param name="Membership">True when its status changed (revoked, re-activated, a tombstone written); false for a version or a field only.</param>
    public sealed record WhitelistRepairChange(Guid NodeId, string Before, string After, bool Membership);

    /// <summary>
    /// The one-off repair of the whitelist (<see cref="WhitelistOrderRepair"/>): every whitelist event the log holds, peer by peer and in version
    /// order, through the rules above, as if each arrived now. Before that, an active row written before migration 045 (all its concerns at the
    /// row's one version) gives a concern the version of the event that really wrote it, where the log shows which one that was
    /// (<see cref="SplitConcernVersionsAsync"/>): then a revoke or a demotion that an update crossed under an older build, and that build dropped,
    /// applies now. A row the rules leave as it is stays so, and a second run changes nothing. A peer with no row is given at most a revoked one,
    /// and only when the newest add or revoke the log holds for it is a revoke (no row may be a decision: a restore that rebuilt the whitelist, a
    /// join rolled back); a peer with no whitelist event in the log is not touched (below a compaction checkpoint, or for a row a join wrote, the
    /// row is all the evidence there is).
    /// </summary>
    /// <returns>Every row that changed: before and after (status, version, name, authority; never an address).</returns>
    public async Task<IReadOnlyList<WhitelistRepairChange>> RepairWhitelistOrderAsync(CancellationToken ct = default)
    {
        using var _ = await EventWriteGate.Instance.EnterAsync(ct);
        using var ownerFlow = EventWriteGate.EnterOwnerFlow();

        var self = (await nodeIdentityRepo.GetAsync())?.NodeId;
        var historyStart = await HistoryStartAsync();
        var changes = new List<WhitelistRepairChange>();
        foreach (var peer in await WhitelistHistory.PeersAsync(connFactory))
        {
            ct.ThrowIfCancellationRequested();
            if (peer == self) continue;
            var before = await whitelistRepo.GetByNodeIdAsync(peer, includeDeleted: true);
            var facts = await WhitelistHistory.ForPeerAsync(connFactory, peer);
            if (before is null)
            {
                var newest = facts.Where(f => f.EventType is EventTypes.WhitelistAdd or EventTypes.WhitelistRevoke).LastOrDefault();
                if (newest is not { EventType: EventTypes.WhitelistRevoke }) continue;
                facts = [newest];
            }
            else if (before.Status == WhitelistStatuses.Active)
            {
                try { await SplitConcernVersionsAsync(peer, facts, historyStart); }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
                {
                    // A payload that does not read: the versions stay as they are, and the events run through the rules below as for any row.
                    logger.LogWarning(ex, "Whitelist repair: the versions of {NodeId} are left as they are: an event of it does not read", peer);
                }
            }
            foreach (var fact in facts)
            {
                try
                {
                    switch (fact.EventType)
                    {
                        case EventTypes.WhitelistAdd: await ApplyWhitelistAddAsync(fact.ToEvent(), repairing: true); break;
                        case EventTypes.WhitelistRevoke: await ApplyWhitelistRevokeAsync(fact.ToEvent(), repairing: true); break;
                        case EventTypes.WhitelistUpdate: await ApplyWhitelistUpdateAsync(fact.ToEvent(), repairing: true); break;
                    }
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
                {
                    // An event whose payload does not read (it applied once, so this is damage, not a rule) must not hold back the rest. Anything
                    // else (the database) stops the run before its marker is written, and the next start runs it again.
                    logger.LogWarning(ex, "Whitelist repair: event {EventId} ({Type}) for {NodeId} skipped", fact.EventId, fact.EventType, peer);
                }
            }
            var after = await whitelistRepo.GetByNodeIdAsync(peer, includeDeleted: true);
            var (was, now) = (Describe(before), Describe(after));
            if (was != now) changes.Add(new WhitelistRepairChange(peer, was, now, before?.Status != after?.Status));
        }
        return changes;

        static string Describe(WhitelistEntry? row) => row is null
            ? "no row"
            : $"{row.Status} at ({row.LamportTs}, {row.SourceNodeId ?? Guid.Empty}), \"{new string(row.DisplayName.Where(c => !char.IsControl(c)).Take(64).ToArray())}\""
              + (row.IsSuperadmin ? ", superadmin" : "") + (row.Ed25519PublicKey.Length == 0 ? ", no key yet" : "");
    }

    /// <summary>
    /// An active row from before migration 045 has its one version on every concern, its fields undated. A field the event that gave the row
    /// its version wrote (with the value the row has) is dated at that version. Where that version is provably not a concern's, the concern
    /// moves back to the version of the event that wrote its value, dated, and the events after that one (the facts run next) are judged against it.
    /// That is what lets a revoke, or a demotion, that an update crossed under an older build apply now: the update gave the row its version, and
    /// the revoke, older than that, was dropped although nothing newer had written membership. Each condition below keeps the repair from
    /// revoking or demoting on half the evidence (a log that does not hold the row's whole history), and from ever adding or promoting:
    /// <list type="bullet">
    /// <item>the event that gave the row its version is in the log, and does not write the concern (else that version IS the concern's);</item>
    /// <item>the concern still has the row's version (a concern this build versioned is left as it is);</item>
    /// <item>the row's value raises trust or is neutral: membership of an active row, superadmin when it is set, the name, the address, the TLS
    /// trust. Not superadmin when it is clear: moving it back could only let an older promotion in;</item>
    /// <item>the log holds the event that wrote the row's value: the newest one older than the row's version that writes the concern with the
    /// value the row has. The concern takes its version; with none, the value came from outside the log and nothing moves;</item>
    /// <item>every event between that one and the row's version that writes the concern (with another value, or it would be the newest such)
    /// is an update or a revoke that arrived after an event of the log that is newer than it, no newer than the row's version, and does not write
    /// the concern: what an older build dropped it for, and not for an add or a write this log does not hold;</item>
    /// <item>the row is younger than every cut of this node's history: the last compaction of its log (or a re-key's reset of it), and the last
    /// join or recovery restore (<see cref="LamportFloor"/>), after which the log holds everything this node applied to the row. A row written by
    /// a join or a restore, or older than a compaction, keeps its version: what the host or the backup knew, or what was compacted, may be a
    /// newer write than anything the log holds.</item>
    /// </list>
    /// A concern held back by the last two conditions while a revoke or a demotion is waiting is logged as a warning: an admin decides it.
    /// </summary>
    private async Task SplitConcernVersionsAsync(Guid peer, List<WhitelistFact> facts, DateTime? historyStart)
    {
        var row = await whitelistRepo.GetByNodeIdAsync(peer, includeDeleted: true);
        if (row is not { Status: WhitelistStatuses.Active }) return;
        var version = row.Version;
        var stamped = facts.FirstOrDefault(f => f.Version == version);
        if (stamped is null || version.LamportTs == 0) return;
        var wholeHistory = historyStart is not { } start || row.CreatedAt.ToUniversalTime() > start;

        var moved = WhitelistConcerns.None;
        foreach (var concern in WhitelistConcernSet.Each)
        {
            // Only what migration 045 left: membership at the row's version, a field undated at it. A concern this build versioned stays.
            var fromMigration = row.VersionOf(concern) == version && (concern == WhitelistConcerns.Membership || row.IsUndated(concern));
            if (!fromMigration) continue;
            if (Writes(stamped, concern))
            {
                // The event that gave the row its version wrote this field, with the value the row has: dated at that version.
                if (concern != WhitelistConcerns.Membership && GivesRowValue(stamped, concern, row))
                {
                    row.SetVersionOf(concern, version);
                    moved |= concern;
                }
                continue;
            }
            if (concern == WhitelistConcerns.Superadmin && !row.IsSuperadmin) continue;

            var older = facts.Where(f => WhitelistHistory.Compare(f.Version, version) < 0 && Writes(f, concern)).ToList();
            var source = older.LastOrDefault(f => GivesRowValue(f, concern, row));
            if (source is null) continue;
            var between = older.Where(f => WhitelistHistory.Compare(f.Version, source.Version) > 0).ToList();
            var dropped = between.All(d => d.EventType != EventTypes.WhitelistAdd && facts.Any(e =>
                e.Sequence < d.Sequence
                && WhitelistHistory.Compare(e.Version, d.Version) > 0 && WhitelistHistory.Compare(e.Version, version) <= 0
                && !Writes(e, concern)));
            if (!dropped || !wholeHistory)
            {
                if (between.Count > 0 && concern is WhitelistConcerns.Membership or WhitelistConcerns.Superadmin)
                    logger.LogWarning(
                        "Whitelist repair: {NodeId}: the log holds a {What} older than the row's version that a later event may have crossed, but {Why}; "
                        + "the row is left as it is. If the peer should be {Outcome}, do it under Admin > Devices, on the device page.",
                        peer, concern == WhitelistConcerns.Membership ? "revoke" : "demotion",
                        wholeHistory ? "the log does not show what it lost to" : "the row's history is not all in this node's log (compacted, or written by a join or a restore)",
                        concern == WhitelistConcerns.Membership ? "revoked" : "demoted");
                continue;
            }
            row.SetVersionOf(concern, source.Version);
            moved |= concern;
        }
        if (moved == WhitelistConcerns.None) return;

        SetRowVersionToNewestConcern(row);
        await whitelistRepoWrite.UpdateAsync(row);
        logger.LogInformation("Whitelist repair: {NodeId}: {Concerns} now carry the version of the event that wrote them, not ({RowTs}, {RowNode})",
            peer, moved, version.LamportTs, version.SourceNodeId);
    }

    /// <summary>
    /// The latest cut in this node's history: the newest compaction of its log (a re-key's reset and a blind node's trim record one too), and the
    /// newest join or recovery restore (they raise <see cref="LamportFloor"/>). Null when there was none. Local times of this node, as the rows'
    /// <c>created_at</c>.
    /// </summary>
    private async Task<DateTime?> HistoryStartAsync()
    {
        using var conn = connFactory.CreateConnection();
        var stamps = (await conn.QueryAsync<string?>("SELECT compacted_at FROM tbl_compaction_log")).ToList();
        stamps.AddRange(await conn.QueryAsync<string?>("SELECT set_at FROM tbl_migration_marker WHERE key = @Key", new { LamportFloor.Key }));
        DateTime? latest = null;
        foreach (var stamp in stamps)
        {
            if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            {
                // A stamp that does not read is a cut at an unknown time: no row is known to be younger than it.
                return DateTime.MaxValue;
            }
            if (latest is null || at > latest) latest = at;
        }
        return latest;
    }

    /// <summary>Whether <paramref name="fact"/> writes <paramref name="concern"/>, as the applier reads it.</summary>
    private static bool Writes(WhitelistFact fact, WhitelistConcerns concern) => fact.EventType switch
    {
        EventTypes.WhitelistAdd => true,
        EventTypes.WhitelistRevoke => concern == WhitelistConcerns.Membership,
        EventTypes.WhitelistUpdate => UpdateConcerns(fact).HasFlag(concern),
        _ => false,
    };

    private static WhitelistConcerns UpdateConcerns(WhitelistFact fact)
    {
        var p = Deserialize<WhitelistUpdatePayload>(fact.Payload);
        return WhitelistEventConcerns.OfUpdate(p, fact.Version.SourceNodeId, WhitelistEventConcerns.StoredAddress(p.ApiAddress), out _);
    }

    /// <summary>Whether <paramref name="fact"/> (which writes <paramref name="concern"/>) gives it the value <paramref name="row"/> has.</summary>
    private static bool GivesRowValue(WhitelistFact fact, WhitelistConcerns concern, WhitelistEntry row)
    {
        if (concern == WhitelistConcerns.Membership)
            return fact.EventType == EventTypes.WhitelistAdd && row.Status == WhitelistStatuses.Active;
        var value = new WhitelistEntry();
        switch (fact.EventType)
        {
            case EventTypes.WhitelistAdd:
                var add = Deserialize<WhitelistAddPayload>(fact.Payload);
                value.NodeId = add.NodeId;
                ApplyAddFields(value, add, WhitelistEventConcerns.StoredAddress(add.ApiAddress));
                break;
            case EventTypes.WhitelistUpdate:
                var update = Deserialize<WhitelistUpdatePayload>(fact.Payload);
                // The address is compared as it is, not put on the scratch row: the writers of an address are few and named (PeerAddressWritersInventoryTests).
                if (concern == WhitelistConcerns.Address) return WhitelistEventConcerns.StoredAddress(update.ApiAddress) == row.ApiAddress;
                value.DisplayName = update.DisplayName ?? "";
                value.IsSuperadmin = update.IsSuperadmin ?? false;
                ApplyTlsTrust(value, update.TlsTrust, update.TlsSpki, isAdd: false);
                break;
            default:
                return false;
        }
        return SameValue(concern, value, row);
    }

    private void LogWhitelistDrop(bool repairing, string eventType, Guid nodeId, RowVersion row, RowVersion incoming)
    {
        const string Message = "{Type} for {NodeId} dropped: row version ({RowTs}, {RowNode}) wins over event ({EventTs}, {EventNode})";
        if (repairing)
            logger.LogDebug(Message, eventType, nodeId, row.LamportTs, row.SourceNodeId, incoming.LamportTs, incoming.SourceNodeId);
        else
            logger.LogInformation(Message, eventType, nodeId, row.LamportTs, row.SourceNodeId, incoming.LamportTs, incoming.SourceNodeId);
    }

    /// <summary>
    /// The pin and the trust mode a whitelist event carries, onto <paramref name="row"/> (ADR 0007). The mode is
    /// the authority: <c>public-ca</c> never keeps a pin, <c>none</c> clears both, <c>pin</c> needs a key in the event,
    /// and a mode this build does not know is kept as written (so it travels on to builds that know it) with no pin —
    /// the row is then not callable here, and never read as <c>pin</c>. Only an event without a mode at all (an older
    /// sender) is read as before the mode existed: a pin sets a pin (so the row stands as <c>pin</c>), "" removes it,
    /// and on an update null leaves everything as it is.
    /// </summary>
    private const int MaxUnknownTrustLength = 32;

    private static void ApplyTlsTrust(WhitelistEntry row, string? trust, string? tlsSpki, bool isAdd)
    {
        switch (trust)
        {
            case BlindTrust.PublicCa:
                row.TlsSpki = null;
                row.TlsTrust = BlindTrust.PublicCa;
                return;
            case BlindTrust.None:
                row.TlsSpki = null;
                row.TlsTrust = null;
                return;
            case BlindTrust.Pin when !string.IsNullOrEmpty(tlsSpki):
                row.TlsSpki = tlsSpki;
                row.TlsTrust = BlindTrust.Pin;
                return;
        }

        if (!string.IsNullOrEmpty(trust) && trust != BlindTrust.Pin)
        {
            row.TlsSpki = null;
            row.TlsTrust = trust.Length <= MaxUnknownTrustLength ? trust : "unknown";
            return;
        }

        if (tlsSpki == null && !isAdd) return;
        row.TlsSpki = string.IsNullOrEmpty(tlsSpki) ? null : tlsSpki;
        if (row.TlsSpki != null) row.TlsTrust = BlindTrust.Pin;
        else if (isAdd || row.TlsTrust == BlindTrust.Pin) row.TlsTrust = null;
    }

    /// <summary>
    /// The address a replicated whitelist event may put on a row: the one policy's canonical form (<see cref="PeerAddressPolicy.ForStorage"/>), or none. An event is a
    /// message from another node, signed but not vouched for as to WHERE it tells this node to connect: an address with a user name, a query, or a link-local or
    /// metadata target is not stored, the row keeps what it had (or has none), and the warning names the peer, never the address.
    /// </summary>
    private string? AddressFromEvent(Guid nodeId, string? incoming, bool warn = true)
    {
        if (string.IsNullOrWhiteSpace(incoming)) return null;
        var stored = PeerAddressPolicy.ForStorage(incoming);
        if (stored is null && warn)
            logger.LogWarning("A whitelist event for {NodeId} carried an address the address policy refuses; it was not stored (the peer keeps the address it had, or has none).", nodeId);
        return stored;
    }

    private async Task ApplyMasterPasswordChangedAsync(SyncEvent evt)
    {
        var p = Deserialize<MasterPasswordChangedPayload>(evt.Payload);

        // A notice older than this node's own last password change describes a gap that is
        // already closed: the peer changed at T1, this node was offline or was changed by hand at
        // T2 > T1, and the event only arrives now. Raising the banner for it would tell the admin
        // to redo something they have already done.
        //
        // The opposite order — a peer changing AFTER this node did — is deliberately NOT filtered.
        // Nothing in the event says whether the peer moved to the same password or a different
        // one, so a timestamp cannot tell "the operator is working through the mesh" from "someone
        // changed the password on a machine I do not control". The banner is raised and the admin
        // dismisses it (POST /api/keys/password-notice/dismiss); see migration 019 for why a
        // password verifier in the payload is not the answer.
        var localChangedAt = await nodeIdentityRepo.GetMasterPasswordChangedLocallyAtAsync();
        if (localChangedAt.HasValue && p.ChangedAt <= localChangedAt.Value)
        {
            logger.LogInformation(
                "Ignoring master_password_changed from {NodeName} at {ChangedAt}: this node changed its own master password later, at {LocalChangedAt}.",
                p.NodeName, p.ChangedAt, localChangedAt.Value);
            return;
        }

        // Nothing to rewrap: the event carries no key material by design (see the payload). All we
        // can do — and all we should do — is remember that this node is now out of step, so the
        // admin UI can say so and an admin can enter the new password here too.
        await nodeIdentityRepo.SetMasterPasswordNoticeAsync(p.ChangedAt, p.NodeName);

        logger.LogWarning(
            "Master password was changed on node {NodeName} at {ChangedAt}. This node still accepts the OLD password, including at its own /api/join, until an admin changes it here.",
            p.NodeName, p.ChangedAt);
    }

    private async Task ApplyCommentCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentEventPayload>(evt.Payload);

        var existing = await commentRepo.GetByCommentIdAsync(p.CommentId);

        // Downgrade-prevention: refuse a brand-new unencrypted comment under an encrypted
        // article body. Skip this gate for existing-row paths (LWW resurrect / update),
        // which preserve the originally-encrypted payload anyway.
        if (existing == null)
        {
            var parentBody = await bodyRepo.GetByArticleIdAsync(p.ArticleId);
            if (parentBody != null && !p.Encrypted)
            {
                logger.LogWarning("Rejecting unencrypted comment for encrypted article {ArticleId} (downgrade attempt)", p.ArticleId);
                return;
            }
        }

        if (existing?.DeletedAt != null)
        {
            // Soft-deleted row (tombstone equivalent): check LWW
            // If delete's lamport >= create's lamport, dead stays dead.
            if ((existing.DeleteLamportTs ?? 0) >= evt.LamportTs)
                return;

            // Create wins: resurrect the row with real data
            await commentRepo.ResurrectFromSyncAsync(p.CommentId, new Comment
            {
                CommentId = p.CommentId,
                ArticleId = p.ArticleId,
                Text = p.Encrypted ? "" : p.Text,
                SourceNodeId = evt.NodeId,
                CreatedAt = p.CreatedAt,
                Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
                IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
                Encrypted = p.Encrypted,
                LamportTs = evt.LamportTs
            });
            return;
        }

        if (existing != null)
        {
            // Alive row: LWW
            if (!ConflictResolver.IncomingWins(
                    RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
                return;
            // LWW-wins must update CONTENT too — bumping only the lamport would leave stale
            // text/ciphertext attached to a newer timestamp, and future comparisons would see
            // the stale content as "newer".
            await commentRepo.ResurrectFromSyncAsync(p.CommentId, new Comment
            {
                CommentId = p.CommentId,
                ArticleId = p.ArticleId,
                Text = p.Encrypted ? "" : p.Text,
                SourceNodeId = evt.NodeId,
                CreatedAt = p.CreatedAt,
                Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
                IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
                Encrypted = p.Encrypted,
                LamportTs = evt.LamportTs
            });
            return;
        }

        // No row: create
        await commentRepo.CreateFromSyncAsync(new Comment
        {
            CommentId = p.CommentId,
            ArticleId = p.ArticleId,
            Text = p.Encrypted ? "" : p.Text,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
            IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
            Encrypted = p.Encrypted,
            LamportTs = evt.LamportTs
        });
    }

    private async Task ApplyCommentDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentDeletePayload>(evt.Payload);
        var existing = await commentRepo.GetByCommentIdAsync(p.CommentId);

        if (existing == null)
        {
            // Comment not on this node yet — insert placeholder ghost row so future
            // CommentCreate events can be blocked if delete has higher lamport.
            await commentRepo.SoftDeletePlaceholderAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            return;
        }

        if (existing.DeletedAt != null)
        {
            // Already soft-deleted — keep the winning delete, not merely the higher lamport.
            // tbl_comment stores delete_node_id alongside delete_lamport_ts precisely so this
            // comparison can be made the same way as every other one.
            if (ConflictResolver.IncomingWins(
                    RowVersion.Of(existing.DeleteLamportTs ?? 0, existing.DeleteNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
            {
                await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            }
            return;
        }

        // Alive comment: LWW
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return; // Delete loses to existing create

        // Delete wins: soft-delete
        await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
    }
}
