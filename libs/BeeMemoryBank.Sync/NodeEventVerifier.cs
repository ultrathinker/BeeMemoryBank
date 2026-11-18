using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync;

/// <summary>Where a signature of a node is checked (plan WS-D D3), which decides what a retired key of that node may still sign.</summary>
public enum NodeKeyMode
{
    /// <summary>
    /// Live sync, quarantine replays, relays: a retired key signs an event only when the event is above the rotation's checkpoint, at or below the
    /// rotation's own lamport, and its id is on the rotation's list. Everything below the checkpoint is refused: a peer that lacks such an event is
    /// behind the checkpoint and joins again, as before.
    /// </summary>
    Live,

    /// <summary>
    /// A signed package (blind or backup-folder delta replay, a baseline, a snapshot manifest) or this node's own echoed events: a retired key signs
    /// what is at or below the rotation's own lamport, the list is not asked.
    /// </summary>
    Package,
}

/// <summary>
/// THE helper that verifies every signature of a node (release 2.12, plan WS-D D3): the node's current key, or one of its retired keys under the
/// rule of <see cref="NodeKeyMode"/>. Every verification site of plan section 1.4 calls it; nothing else decides which key of a node is good.
/// <para>
/// Why a Lamport bound alone is not enough: a peer that has not applied the rotation yet still trusts the old key, so a thief who holds the old
/// seed can push back-dated events to it, and it relays them. The list of the node's own events above its checkpoint, carried in the rotation
/// signed by the node itself, is what tells those apart from the node's genuine stragglers.
/// </para>
/// </summary>
public static class NodeEventVerifier
{
    // The unpacked lists, by rotation event: a history row never changes, so neither does its list. Bounded: cleared when it grows past the cap.
    private static readonly ConcurrentDictionary<Guid, HashSet<Guid>> Lists = new();
    private const int MaxCachedLists = 64;

    /// <summary>True when <paramref name="evt"/>'s signature is its author's under <paramref name="currentKey"/> or an admissible retired key.</summary>
    public static bool Verify(SyncEvent evt, byte[]? currentKey, IReadOnlyList<NodeKeyHistoryEntry> history, NodeKeyMode mode)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var payload = EventSignature.BuildPayload(evt);
        if (SignatureVerifies(currentKey, payload, evt.Signature)) return true;
        foreach (var row in history)
        {
            if (row.NodeId != evt.NodeId || !Admits(row, evt.LamportTs, evt.EventId, mode)) continue;
            if (SignatureVerifies(row.PublicKey, payload, evt.Signature)) return true;
        }
        return false;
    }

    /// <summary>
    /// The rule for checking <paramref name="evt"/> again: <see cref="NodeKeyMode.Package"/> when this node's log already holds
    /// the very same event (same id, author, body and signature: it passed the Live check when it was received), <see cref="NodeKeyMode.Live"/> at
    /// first delivery. A commit held across its author's re-key falls below the rotation's checkpoint; the Live rule would refuse it for ever.
    /// </summary>
    public static async Task<NodeKeyMode> ReceiptModeAsync(IEventLogRepository log, SyncEvent evt) =>
        await log.GetByIdAsync(evt.EventId.ToString("D")) is { } held && held.NodeId == evt.NodeId && held.LamportTs == evt.LamportTs
        && string.Equals(held.EventType, evt.EventType, StringComparison.Ordinal) && string.Equals(held.Payload, evt.Payload, StringComparison.Ordinal)
        && held.Signature is { Length: > 0 } && held.Signature.AsSpan().SequenceEqual(evt.Signature)
            ? NodeKeyMode.Package
            : NodeKeyMode.Live;

    /// <summary>As <see cref="Verify(SyncEvent, byte[], IReadOnlyList{NodeKeyHistoryEntry}, NodeKeyMode)"/>, with the author's history read from <paramref name="db"/>.</summary>
    public static async Task<bool> VerifyAsync(IDbConnectionFactory db, SyncEvent evt, byte[]? currentKey, NodeKeyMode mode)
    {
        // The common case first: the current key. The history is read only for an event the current key does not verify.
        if (SignatureVerifies(currentKey, EventSignature.BuildPayload(evt), evt.Signature)) return true;
        using var conn = db.CreateConnection();
        var history = await NodeKeyHistoryStore.ListAsync(conn, null, evt.NodeId);
        return history.Count > 0 && Verify(evt, null, history, mode);
    }

    /// <summary>
    /// A node's signature over something that is not a sync event (a baseline or snapshot manifest, a package, plan D4): the current key, or a retired
    /// one when the thing was signed no later than the rotation that retired it (<paramref name="signedAtLamport"/>, the package's watermark; null
    /// when the format carries none, then a retired key is accepted only up to its own rotation's lamport, which is what an offline restore of an
    /// older package needs). Packages only: never use this for live sync.
    /// </summary>
    public static bool VerifyPackageSignature(Guid nodeId, byte[]? currentKey, IReadOnlyList<NodeKeyHistoryEntry> history, byte[] message,
        byte[] signature, long? signedAtLamport)
    {
        if (SignatureVerifies(currentKey, message, signature)) return true;
        foreach (var row in history)
        {
            if (row.NodeId != nodeId) continue;
            if (signedAtLamport is { } at && at > row.ValidUntilLamport) continue;
            if (SignatureVerifies(row.PublicKey, message, signature)) return true;
        }
        return false;
    }

    public static async Task<bool> VerifyPackageSignatureAsync(IDbConnectionFactory db, Guid nodeId, byte[]? currentKey, byte[] message,
        byte[] signature, long? signedAtLamport)
    {
        if (SignatureVerifies(currentKey, message, signature)) return true;
        using var conn = db.CreateConnection();
        var history = await NodeKeyHistoryStore.ListAsync(conn, null, nodeId);
        return history.Count > 0 && VerifyPackageSignature(nodeId, null, history, message, signature, signedAtLamport);
    }

    /// <summary>
    /// The rows of a history that came with untrusted data (a package's control part) which may be used for <paramref name="nodeId"/>: only those
    /// whose new key leads, through links both keys signed, to <paramref name="trustedKey"/>, the key this receiver trusts for the node (plan D4).
    /// A row a thief made with a stolen old seed (old key to the thief's key) never leads to the genuine key, so it is never admitted.
    /// </summary>
    public static IReadOnlyList<NodeKeyHistoryEntry> ChainedTo(Guid nodeId, byte[]? trustedKey, IEnumerable<NodeKeyHistoryEntry> rows)
    {
        if (trustedKey is not { Length: NodeKeyTransition.KeySize }) return [];
        var own = rows.Where(r => r.NodeId == nodeId).ToList();
        var links = own.Select(r => r.Link).ToList();
        return own.Where(r => NodeKeyTransition.VerifyLink(nodeId, r.Link)
                              && NodeKeyTransition.Advance(nodeId, r.NewPublicKey, links).AsSpan().SequenceEqual(trustedKey))
            .ToList();
    }

    /// <summary>The D3 rule for one retired key.</summary>
    public static bool Admits(NodeKeyHistoryEntry row, long lamportTs, Guid eventId, NodeKeyMode mode)
    {
        if (lamportTs > row.ValidUntilLamport) return false;
        if (mode == NodeKeyMode.Package) return true;
        if (lamportTs <= row.CheckpointLamportTs) return false;
        return ListOf(row).Contains(eventId);
    }

    private static HashSet<Guid> ListOf(NodeKeyHistoryEntry row)
    {
        if (Lists.TryGetValue(row.RotationEventId, out var cached)) return cached;
        HashSet<Guid> set;
        try { set = EventIdSet.Unpack(row.OwnEventIds); }
        catch (FormatException) { set = []; }   // a malformed list admits nothing
        if (Lists.Count >= MaxCachedLists) Lists.Clear();
        Lists[row.RotationEventId] = set;
        return set;
    }

    /// <summary>An Ed25519 check that never throws for a key or signature of the wrong size.</summary>
    public static bool SignatureVerifies(byte[]? publicKey, byte[] message, byte[]? signature)
    {
        if (publicKey is not { Length: NodeKeyTransition.KeySize } || signature is not { Length: NodeKeyTransition.SignatureSize }) return false;
        try { return Ed25519Signer.Verify(publicKey, message, signature); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}
