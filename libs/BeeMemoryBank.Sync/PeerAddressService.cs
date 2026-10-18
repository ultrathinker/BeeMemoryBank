using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public enum PeerAddressOutcome
{
    /// <summary>The row carries the new address and the other nodes were told.</summary>
    Changed,

    /// <summary>The peer already has this address (after canonicalisation): nothing was probed, written or published.</summary>
    Unchanged,

    NotFound,
    OwnNode,

    /// <summary>The address is not acceptable (<see cref="PeerAddressPolicy"/>).</summary>
    Refused,

    /// <summary>Something answers there, but it is not a sync server (<see cref="NoSyncServerException"/>).</summary>
    NoSyncServer,

    /// <summary>Nothing answered in a way that says what is there. Saved only when the caller said it may be (the person was asked).</summary>
    NoAnswer,

    /// <summary>A sync server answers, with another node id than the one chosen.</summary>
    WrongNode,

    /// <summary>The right node id, another public key than the one on record.</summary>
    WrongKey,

    /// <summary>Another peer has this address.</summary>
    InUse,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Message">A sentence for the person; it never quotes the address.</param>
/// <param name="Entry">The peer's row (as it is now), when there is one.</param>
public sealed record PeerAddressResult(PeerAddressOutcome Outcome, string Message, WhitelistEntry? Entry = null)
{
    public bool Succeeded => Outcome is PeerAddressOutcome.Changed or PeerAddressOutcome.Unchanged;
}

/// <summary>
/// What a workflow that knows the peer by its own proof (the master password of a re-join, the pair code of a blind node, the key a hub probe read) asks its row to
/// take. Every member is "as it is on the whitelist_update event": null leaves it alone.
/// </summary>
/// <param name="RawAddress">The address the proof names, as it came in; null or blank keeps the address on record.</param>
/// <param name="ProvenKey">The public key the proof names. It must be the key on record: the key a row is dialled by never changes through an address or a pairing change.</param>
/// <param name="DisplayName">A new display name.</param>
/// <param name="IsSuperadmin">A new superadmin flag.</param>
/// <param name="TlsSpki">The pin of the key the peer is dialled by; "" removes it.</param>
/// <param name="TlsTrust">The trust mode (<see cref="BlindTrust"/>); <see cref="BlindTrust.None"/> clears it.</param>
/// <param name="PairingChanged">True when the pairing generation changed (a blind node paired again) even if nothing above did: the event is still published and the loop forgets the peer.</param>
/// <param name="Announce">True when the event is to be published even if the row already says all of this (a re-join: the known address is announced, so every node ends up with the same one). Nothing is reset or audited then.</param>
public sealed record PeerProof(
    string? RawAddress, byte[]? ProvenKey = null, string? DisplayName = null, bool? IsSuperadmin = null,
    string? TlsSpki = null, string? TlsTrust = null, bool PairingChanged = false, bool Announce = false);

/// <summary>
/// The ONE writer of a peer's address on a running node (BMB-307 fix rounds). It has three named entry points, and every assignment of a row's <c>ApiAddress</c> on a
/// live node is in one of them (<c>PeerAddressWriterArchitectureTests</c> fails for any other; the sites that build rows from an authoritative source, and the
/// event applier, are listed there with the reason each is allowed):
/// <list type="bullet">
/// <item><see cref="ChangeAsync"/>: a person changes a peer's address (the node's "Change node address", the phone's "Edit URL"); the address is probed;</item>
/// <item><see cref="ApplyProofAsync"/>: an existing peer proves itself some other way (a re-join, a pair code, a hub probe) and its row takes the new address, name,
///   flag or pairing;</item>
/// <item><see cref="AdmitAsync"/>: a new peer's row is added.</item>
/// </list>
/// All three share the same rules - the address passes <see cref="PeerAddressPolicy"/> and is stored canonical (or nothing is written and nothing is announced),
/// the key on record never changes, the <c>whitelist_update</c> / <c>whitelist_add</c> event is logged first and the row carries the version it minted, a repeat
/// that changes nothing writes and announces nothing, the loop forgets what it learned about the old address (<see cref="IPeerStateResetter"/>) and the other
/// nodes are signalled, and a change of the place or of the pairing is audited in one line that says who and which peer (the addresses as fingerprints). The rest of
/// this comment is <see cref="ChangeAsync"/>'s order:
/// <list type="number">
/// <item>the peer exists, is active and is not this node;</item>
/// <item>the address passes <see cref="PeerAddressPolicy"/> and is made canonical - nothing is sent anywhere before that;</item>
/// <item>the same canonical address as the row has is a no-op: no probe, no event, no audit line;</item>
/// <item>no other peer has it;</item>
/// <item>the address is probed with a client that follows no redirect, and a sync server must answer there with the chosen node id AND the public key on
/// record (constant time; the key never changes through an address change);</item>
/// <item>the <c>whitelist_update</c> event is logged first and the row written with the version it minted; the loop's memory of the peer is forgotten
/// (<see cref="IPeerStateResetter"/>, which also signals a sync) and the other nodes are told;</item>
/// <item>an audit line says who, which peer, and the old and new address as short fingerprints - never the address.</item>
/// </list>
/// Who may ask (superadmin, not an agent, the master password) is the caller's gate: a screen cannot be asked to re-authenticate by a library.
/// </summary>
public sealed class PeerAddressService(
    IWhitelistRepository whitelist,
    INodeIdentityRepository identityRepo,
    IEventLogger events,
    IAuditLogRepository audit,
    IHttpClientFactory httpFactory,
    ILogger<PeerAddressService> logger,
    IPeerStateResetter? peerState = null)
{
    /// <summary>The named client the probe uses: it follows no redirect and pins like every client that dials a sync peer (<c>AddNodeSync</c>).</summary>
    public const string HttpClientName = "bmb-address-probe";

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static bool SamePlace(string canonicalAddress, string? stored) =>
        !string.IsNullOrWhiteSpace(stored)
        && string.Equals(canonicalAddress, PeerAddressPolicy.Canonicalize(stored).Address ?? stored.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <param name="nodeId">The peer.</param>
    /// <param name="rawAddress">What was typed or sent.</param>
    /// <param name="actor">Who asked (a user id, or "phone"): for the audit line.</param>
    /// <param name="via">Where the request came from (<c>web</c>, <c>phone</c>): the audit line's actor type.</param>
    /// <param name="saveWhenUnanswered">True only after the person was told that nothing answers there and chose to save it anyway (an address filled in before its server is up).</param>
    public async Task<PeerAddressResult> ChangeAsync(
        Guid nodeId, string? rawAddress, string actor, string via, bool saveWhenUnanswered = false, CancellationToken ct = default)
    {
        var entry = await whitelist.GetByNodeIdAsync(nodeId, includeDeleted: true);
        if (entry is null || entry.Status != "A")
            return new(PeerAddressOutcome.NotFound, $"Node {nodeId} not found in whitelist");

        var self = await identityRepo.GetAsync();
        if (self is not null && self.NodeId == nodeId)
            return new(PeerAddressOutcome.OwnNode, "Cannot change the URL of your own node");

        var canonical = PeerAddressPolicy.Canonicalize(rawAddress);
        if (!canonical.Ok) return new(PeerAddressOutcome.Refused, canonical.Refusal!);
        var address = canonical.Address!;

        // A stored address is compared in the same canonical form (rows written before the policy may carry a default port or a trailing slash).
        if (SamePlace(address, entry.ApiAddress))
            return new(PeerAddressOutcome.Unchanged, "The device already has this address.", entry);

        var conflict = (await whitelist.GetAllActiveAsync()).FirstOrDefault(e => e.NodeId != nodeId && SamePlace(address, e.ApiAddress));
        if (conflict is not null)
            return new(PeerAddressOutcome.InUse, $"This address is already used by node {conflict.DisplayName}");

        var unverified = false;
        using (var http = httpFactory.CreateClient(HttpClientName))
        {
            http.Timeout = ProbeTimeout;
            var check = await SyncAddress.CheckAsync(http, address, ct);
            switch (check.Verdict)
            {
                case AddressVerdict.NoSyncServer:
                    return new(PeerAddressOutcome.NoSyncServer, check.Message);
                case AddressVerdict.NoAnswer when !saveWhenUnanswered:
                    return new(PeerAddressOutcome.NoAnswer, $"Nothing answered at the new address: {check.Message}");
                case AddressVerdict.NoAnswer:
                    unverified = true;   // the first session that reaches it checks who answers (SyncClient: node id and key)
                    break;
                default:
                    if (check.Identity!.NodeId != nodeId)
                        return new(PeerAddressOutcome.WrongNode,
                            $"NodeId at the new address ({check.Identity.NodeId}) does not match the expected ({nodeId}). This is a different node!");
                    if (!PeerKey.Matches(entry.Ed25519PublicKey, check.Identity.Ed25519PublicKeyB64))
                        return new(PeerAddressOutcome.WrongKey,
                            "The node at the new address has the right id but another key than the one this network knows it by. Not trusting it.");
                    break;
            }
        }

        var before = PeerAddressPolicy.Fingerprint(entry.ApiAddress);

        // Log first: the event mints the version the row has to carry, so that a peer's concurrent address change is judged against what this node announced.
        var version = await events.LogWhitelistUpdateAsync(nodeId, address, null);
        entry.ApiAddress = address;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.LamportTs = version.LamportTs;
        entry.SourceNodeId = version.SourceNodeId;
        await whitelist.UpdateAsync(entry);

        // What the loop learned about the old address does not count for the new one, and the new one is tried at once (this signals a sync too).
        peerState?.PeerChanged(nodeId);
        events.SignalSync();

        try
        {
            await audit.LogAsync("whitelist", nodeId.ToString(), "peer_address_changed", via,
                $"Address of {entry.DisplayName} changed by {actor} (host {before} -> {PeerAddressPolicy.Fingerprint(address)})"
                + (unverified ? "; saved without an answer from it" : ""));
        }
        catch (Exception ex)
        {
            // The change is made and published; a failed audit line must not turn it into an error the person would repeat.
            logger.LogWarning(ex, "The audit line of the address change of {NodeId} could not be written", nodeId);
        }

        return new(PeerAddressOutcome.Changed, unverified ? "Saved. Nothing answers there yet." : "Address updated.", entry);
    }

    /// <summary>
    /// An existing peer proved itself in its own way, and its row takes what the proof names. The same rules as <see cref="ChangeAsync"/>, in the same order, except
    /// for the probe (the proof is the evidence): the peer is active and is not this node; the address passes <see cref="PeerAddressPolicy"/> and is canonical, or
    /// nothing happens and nothing is sent; the key the proof names is the key on record (constant time); a repeat that changes nothing is
    /// <see cref="PeerAddressOutcome.Unchanged"/> (no event, no write, no audit line; unless the proof says <see cref="PeerProof.Announce"/>); otherwise the event is
    /// logged first with the same canonical address the row stores, the row is written with the version it minted, the loop forgets the peer when its place or its
    /// pairing changed and the other nodes are signalled, and a change of the place or the pairing is audited in one line that says who, which peer, and the old and
    /// new address as fingerprints.
    /// </summary>
    /// <param name="entry">The peer's row as the caller read it (under its own gate, if it has one).</param>
    public async Task<PeerAddressResult> ApplyProofAsync(WhitelistEntry entry, PeerProof proof, string actor, string via)
    {
        if (entry.Status != "A")
            return new(PeerAddressOutcome.NotFound, $"Node {entry.NodeId} is not an active peer");

        var self = await identityRepo.GetAsync();
        if (self is not null && self.NodeId == entry.NodeId)
            return new(PeerAddressOutcome.OwnNode, "Cannot change the address or the pairing of your own node");

        var address = entry.ApiAddress;
        if (!string.IsNullOrWhiteSpace(proof.RawAddress))
        {
            var canonical = PeerAddressPolicy.Canonicalize(proof.RawAddress);
            if (!canonical.Ok) return new(PeerAddressOutcome.Refused, canonical.Refusal!);
            address = canonical.Address;
        }

        if (proof.ProvenKey is { } key && !PeerKey.Matches(entry.Ed25519PublicKey, Convert.ToBase64String(key)))
            return new(PeerAddressOutcome.WrongKey, "The key named here is not the key this network knows this node by. Not trusting it.");

        var spki = proof.TlsSpki is null ? entry.TlsSpki : proof.TlsSpki.Length == 0 ? null : proof.TlsSpki;
        var trust = proof.TlsTrust is null ? entry.TlsTrust : proof.TlsTrust == BlindTrust.None ? null : proof.TlsTrust;
        var moved = PeerContactLog.EndpointKey(entry.ApiAddress) != PeerContactLog.EndpointKey(address);
        var pairing = proof.PairingChanged || !string.Equals(entry.TlsSpki, spki, StringComparison.Ordinal) || entry.TlsTrust != trust;
        var renamed = proof.DisplayName is not null && proof.DisplayName != entry.DisplayName;
        var promoted = proof.IsSuperadmin is { } flag && flag != entry.IsSuperadmin;
        if (!moved && !pairing && !renamed && !promoted && !proof.Announce)
            return new(PeerAddressOutcome.Unchanged, "The row already says this.", entry);

        var before = entry.ApiAddress;
        // Log first: the event mints the version the row has to carry, and it names the address the row will store, not the one that came in.
        var version = await events.LogWhitelistUpdateAsync(entry.NodeId, address, proof.DisplayName, proof.IsSuperadmin, proof.TlsSpki, proof.TlsTrust);
        entry.ApiAddress = address;
        if (proof.DisplayName is not null) entry.DisplayName = proof.DisplayName;
        if (proof.IsSuperadmin is { } superadmin) entry.IsSuperadmin = superadmin;
        entry.TlsSpki = spki;
        entry.TlsTrust = trust;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.LamportTs = version.LamportTs;
        entry.SourceNodeId = version.SourceNodeId;
        await whitelist.UpdateAsync(entry);

        // What the loop learned about the old address (or the old pairing) does not count for the new one, and the new one is tried at once.
        if (moved || pairing) peerState?.PeerChanged(entry.NodeId);
        events.SignalSync();
        if (moved || pairing)
            await AuditAsync(entry, moved ? "peer_address_changed" : "peer_repaired", via,
                moved ? $"Address of {entry.DisplayName} changed by {actor} (host {PeerAddressPolicy.Fingerprint(before)} -> {PeerAddressPolicy.Fingerprint(address)})"
                    : $"Pairing of {entry.DisplayName} renewed by {actor}");
        return new(PeerAddressOutcome.Changed, "Updated.", entry);
    }

    /// <summary>
    /// A new peer's row is added on this node. The address passes <see cref="PeerAddressPolicy"/> and the row stores its canonical form (or nothing is written and
    /// nothing is announced), the <c>whitelist_add</c> event is logged first with that same address and the row carries the version it minted, and the loop is told
    /// there is a peer to try (the callers keep the audit lines they always wrote for an added peer). <paramref name="write"/> is how the row reaches the repository when that is more than a create (a peer that was removed and is added
    /// again keeps its old row).
    /// </summary>
    /// <param name="entry">The new row, without an address: this method gives it one.</param>
    /// <param name="rawAddress">The address as it came in; null or blank leaves the row without one.</param>
    public async Task<PeerAddressResult> AdmitAsync(WhitelistEntry entry, string? rawAddress, string actor, string via, Func<WhitelistEntry, Task>? write = null)
    {
        string? address = null;
        if (!string.IsNullOrWhiteSpace(rawAddress))
        {
            var canonical = PeerAddressPolicy.Canonicalize(rawAddress);
            if (!canonical.Ok) return new(PeerAddressOutcome.Refused, canonical.Refusal!);
            address = canonical.Address;
        }

        entry.ApiAddress = address;
        var version = await events.LogWhitelistAddAsync(entry);
        entry.LamportTs = version.LamportTs;
        entry.SourceNodeId = version.SourceNodeId;
        await (write is null ? whitelist.CreateAsync(entry) : write(entry));

        peerState?.PeerChanged(entry.NodeId);
        events.SignalSync();
        return new(PeerAddressOutcome.Changed, "Added.", entry);
    }

    private async Task AuditAsync(WhitelistEntry entry, string action, string via, string details)
    {
        try
        {
            await audit.LogAsync("whitelist", entry.NodeId.ToString(), action, via, details);
        }
        catch (Exception ex)
        {
            // The change is made and published; a failed audit line must not turn it into an error the person would repeat.
            logger.LogWarning(ex, "The audit line of the address change of {NodeId} could not be written", entry.NodeId);
        }
    }
}
