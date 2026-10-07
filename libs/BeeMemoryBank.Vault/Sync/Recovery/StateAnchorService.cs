using System.Data;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>How an anchor check at restore came out (plan 5.5).</summary>
public sealed record AnchorVerification(
    bool Found,
    string? AnchorId,
    string? CreatedAt,
    bool DigestMatches,
    int NewerRows,
    int NewerRowsSigned,
    int UncheckedRows,
    IReadOnlyList<StateDigestEntry> Unverified,
    IReadOnlyList<string> UnconfirmedAnchorDates,
    IReadOnlyList<string>? OtherFormatAnchorDates = null,
    bool HeadProven = true,
    StateDigestHistoryCheck? History = null,
    IReadOnlyList<StateDigestEntry>? Unchecked = null,
    StateDigestTrustCheck? Trust = null,
    string? Digest = null)
{
    /// <summary>
    /// Is this whitelist row vouched for — key and superadmin flag as it stands — by an anchor that matches
    /// (under the proven head key) and whose trust section covers it? Anything else — no matching anchor, or the
    /// row in a differing trust bucket — is not: a restore keeps that row inactive until the user confirms it.
    /// </summary>
    public bool Vouches(Guid nodeId) =>
        MatchesAnchor && Trust != null
        && !Trust.Unconfirmed.Any(e => string.Equals(e.Id, nodeId.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Would this whitelist row - key and superadmin flag as given - be vouched for by the matching anchor's trust section?
    /// The same row check as <see cref="Vouches"/>, decided before the row is written: a restore writes each peer with
    /// its final status, so no moment exists at which a row the anchor does not vouch for is active (review A2-a).
    /// </summary>
    public bool VouchesRow(Guid nodeId, byte[] publicKey, bool superadmin) =>
        MatchesAnchor && Digest != null && StateDigest.TrustProves(Digest, nodeId, publicKey, superadmin);

    /// <summary>
    /// The imported version history and conflict copies against the matching anchor's history section:
    /// "matches", "differs", or "unchecked" when no anchor matches. Reported beside <see cref="State"/>, not
    /// folded into it: history is written per node, so a blind node's legitimately differs from the anchoring
    /// node's (see <see cref="StateDigest"/>).
    /// <para>No item-level claim: "differs" names the history entries present in a differing bucket
    /// (<see cref="StateDigestHistoryCheck.Unverified"/>), but an entry REMOVED from one cannot be named —
    /// the anchor keeps bucket hashes, not an inventory. Only the number of differing buckets says so.</para>
    /// </summary>
    public string HistoryState => !MatchesAnchor || History == null ? "unchecked" : History.Matches ? "matches" : "differs";

    /// <summary>
    /// A superadmin's anchor whose MAC verifies under the restored DEK matches the recomputed digest of
    /// the imported state at its cut. The DEK is the trust root — only the master password opens it —
    /// so this, and nothing the source says about itself, is what vouches for the data.
    /// </summary>
    public bool MatchesAnchor => Found && DigestMatches;

    /// <summary>Everything imported is covered by a matching anchor: no row is newer than it.</summary>
    public bool Confirmed => MatchesAnchor && NewerRows == 0;

    /// <summary>The anchor's time: what "confirmed" means is "the data as of this moment" (see <see cref="StateAnchorService.VerifyAsync"/>).</summary>
    public string? AsOf => MatchesAnchor ? CreatedAt : null;

    /// <summary>
    /// "confirmed" (every bucket compared and equal, nothing newer); "partially_confirmed" (the anchor
    /// matches the buckets it could compare; <see cref="NewerRows"/> later changes are not covered by it —
    /// their signatures are checked against the source's own key list, which is not proof — and the
    /// covered entries sharing a bucket with them, <see cref="Unchecked"/>, were NOT compared: a tamper
    /// among them is not detected, which is why they are named one by one); or "unconfirmed".
    /// </summary>
    public string State => Confirmed ? "confirmed" : MatchesAnchor ? "partially_confirmed" : "unconfirmed";
}

/// <summary>
/// Integrity anchors (plan 5.5): <see cref="PublishAsync"/> on a caught-up superadmin node,
/// <see cref="VerifyAsync"/> at restore.
/// </summary>
public class StateAnchorService(SessionService session, IDbConnectionFactory connFactory, RecoveryEventPublisher publisher)
{
    public async Task<StateAnchorPayload> PublishAsync()
    {
        StateDigestResult state;
        using (var conn = connFactory.CreateConnection())
            state = await StateDigest.ComputeAsync(conn, includeSelfAsSuperadmin: true);

        var dek = session.GetMasterDek();
        try
        {
            var anchorId = Guid.NewGuid().ToString();
            var fingerprint = DekFingerprint.Of(dek);
            var createdAt = DateTime.UtcNow.ToString("O");
            var payload = new StateAnchorPayload(anchorId, fingerprint, state.PositionVector, state.Digest,
                StateAnchorCrypto.ComputeHmac(dek, anchorId, fingerprint, state.PositionVector, state.Digest, createdAt),
                createdAt);
            await publisher.PublishAsync(EventTypes.StateAnchor, payload);
            return payload;
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    /// <summary>
    /// Checks the restored database against the newest anchor that (1) was signed, as a state_anchor
    /// event in <paramref name="events"/>, by a node whose key <paramref name="publicKeys"/> holds — the table
    /// row alone is not trusted, only the signed payload is used — (2) verifies under <paramref name="dek"/>,
    /// and (3) whose signer was a superadmin AT THE ANCHOR'S CUT: the anchor's own trust section, covered by the
    /// MAC, must hold the signer's row as a superadmin (<see cref="StateDigest.TrustProves"/>). The flags the
    /// source holds now do not decide it — a superadmin demoted after its anchor still vouches for what that
    /// anchor covered. (2) is the most recent key only, since an older
    /// key may be known to a revoked device. Every other anchor is only reported by date. Rows newer than
    /// the anchor must each be backed by a signed event of their source; the others are listed.
    /// </summary>
    /// <param name="publicKeys">Ed25519 keys of the mesh's nodes (the restore manifest's whitelist).</param>
    /// <param name="headProven">
    /// <paramref name="dek"/> is the proven head of the recovered keys (<see cref="RecoveredKeys.HeadProven"/>).
    /// Otherwise nothing is confirmed: an anchor under a key that might not be the newest proves nothing.
    /// </param>
    /// <remarks>
    /// <b>Rollback limit.</b> A source can still hand over an OLDER, internally consistent state — an earlier
    /// package, with the boxes, links and anchors it held then. Everything in it verifies, because it was
    /// genuine once; nothing inside a recovery set can prove that a newer one exists. This is inherent, and
    /// is made visible instead: the result is "confirmed as of <see cref="AnchorVerification.AsOf"/>", the
    /// anchor's time, which the wizard shows in large type.
    /// </remarks>
    public static async Task<AnchorVerification> VerifyAsync(
        IDbConnection conn, byte[] dek, IReadOnlyList<SyncEvent> events, IReadOnlyDictionary<Guid, byte[]> publicKeys, bool headProven)
    {
        if (!headProven)
            return new AnchorVerification(false, null, null, false, 0, 0, 0, [], [], HeadProven: false);

        var fingerprint = DekFingerprint.Of(dek);
        var anchors = await conn.QueryAsync<(string AnchorId, string CreatedAt)>(
            "SELECT anchor_id, created_at FROM tbl_state_anchor ORDER BY created_at DESC, lamport_ts DESC");

        var unconfirmed = new List<string>();
        var otherFormat = new List<string>();
        foreach (var row in anchors)
        {
            var signed = SignedAnchor(row.AnchorId, events, publicKeys);
            var a = signed?.Payload;
            if (a == null || a.DekFingerprint != fingerprint
                || !StateAnchorCrypto.Verify(dek, a.AnchorId, a.DekFingerprint, a.PositionVector, a.Digest, a.CreatedAt, a.Hmac))
            {
                unconfirmed.Add(row.CreatedAt);
                continue;
            }

            // A genuine anchor computed under another digest encoding cannot be compared with this one:
            // say so, rather than report a mismatch that is only a format difference.
            if (StateDigest.FormatOf(a.Digest) != StateDigest.FormatVersion)
            {
                otherFormat.Add(a.CreatedAt);
                continue;
            }

            var cut = a.PositionVector.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value, StringComparer.Ordinal);
            var state = await StateDigest.ComputeAsync(conn, cut);
            // The signer, as the anchor itself recorded the mesh at its cut, was a superadmin.
            if (!StateDigest.TrustProves(a.Digest, signed!.Value.Signer, publicKeys[signed.Value.Signer], superadmin: true))
            {
                unconfirmed.Add(row.CreatedAt);
                continue;
            }
            var unverified = state.BeyondCut.Where(r => !IsBackedBySignedEvent(r, events, publicKeys)).ToList();
            return new AnchorVerification(true, a.AnchorId, a.CreatedAt, StateDigest.Matches(a.Digest, state),
                state.BeyondCut.Count, state.BeyondCut.Count - unverified.Count, state.Unchecked.Count, unverified, unconfirmed, otherFormat,
                History: StateDigest.CompareHistory(a.Digest, state), Unchecked: state.Unchecked,
                Trust: StateDigest.CompareTrust(a.Digest, state), Digest: a.Digest);
        }

        return new AnchorVerification(false, null, null, false, 0, 0, 0, [], unconfirmed, otherFormat);
    }

    // The anchor as its author signed it: a state_anchor event from a known node, with a valid signature.
    // Whether that node may vouch for anything is decided by the anchor's own trust section (VerifyAsync).
    private static (StateAnchorPayload Payload, Guid Signer)? SignedAnchor(
        string anchorId, IReadOnlyList<SyncEvent> events, IReadOnlyDictionary<Guid, byte[]> publicKeys)
    {
        foreach (var evt in events)
        {
            if (evt.EventType != EventTypes.StateAnchor || !publicKeys.TryGetValue(evt.NodeId, out var key))
                continue;
            StateAnchorPayload? payload;
            try { payload = JsonSerializer.Deserialize<StateAnchorPayload>(evt.Payload); }
            catch (JsonException) { continue; }
            if (payload is not { PositionVector: not null } || !string.Equals(payload.AnchorId, anchorId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Ed25519Signer.Verify(key, EventSignature.BuildPayload(evt), evt.Signature))
                return (payload, evt.NodeId);
        }
        return null;
    }

    // A row newer than the anchor is accounted for when its source signed an event at exactly that
    // Lamport time naming the row. The signature binds node, time and payload, so a blind node cannot
    // invent the row, move it to another time, or pin it on another node.
    private static bool IsBackedBySignedEvent(
        StateDigestEntry row, IReadOnlyList<SyncEvent> events, IReadOnlyDictionary<Guid, byte[]> publicKeys)
    {
        foreach (var evt in events)
        {
            if (evt.LamportTs != row.LamportTs
                || !string.Equals(evt.NodeId.ToString("D"), row.Source, StringComparison.OrdinalIgnoreCase))
                continue;
            var names = string.Equals(evt.ArticleId?.ToString("D"), row.Id, StringComparison.OrdinalIgnoreCase)
                || evt.Payload.Contains(row.Id, StringComparison.OrdinalIgnoreCase);
            if (names && publicKeys.TryGetValue(evt.NodeId, out var key)
                && Ed25519Signer.Verify(key, EventSignature.BuildPayload(evt), evt.Signature))
                return true;
        }
        return false;
    }
}
