using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

// Appliers of the blind-node recovery events (BMB-43, plan 5.5 and 6.4; payloads in RecoveryPayloads.cs,
// tables in migrations 027 and 030). Nothing here needs the DEK: a blind node runs exactly this code
// and stores bytes it cannot open. What these appliers CAN check is shape and authority — who may
// publish what, which KDF preset a box may name — because a restore that later opens the material
// trusts only what passes its own verification (fingerprints, the anchor), never these rows as such.
public partial class EventApplier
{
    private static readonly Regex HexSha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    // A state digest: "sd<format>:" + hex of its bucket hashes, optionally "/" + the history buckets and "/" + the
    // trust set of 16-byte row hashes, possibly empty (StateDigest; format 7).
    private static readonly Regex StateDigestValue = new(
        "^sd[0-9]{1,4}:[0-9a-f]{64,8192}(/[0-9a-f]{64,8192}(/([0-9a-f]{32}){0,1024})?)?$", RegexOptions.CultureInvariant);
    private static readonly Regex SealedSecretName = new(
        "^(restic|android-backup):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Bounds on what a single event may carry, so a peer cannot bloat every node's database with one
    // event. A retire names the handful of device boxes one cleanup finds; an anchor's vector has one
    // entry per node that ever authored an event.
    private const int MaxRetireTargets = 64;
    private const int MaxAnchorVectorEntries = 4096;
    private const int MaxSealedSecretBytes = 4096;
    // Superseded and retired boxes are kept (a late event must find its row and stay superseded), though
    // without their key material (PurgeInactiveBoxMaterialAsync), and a peer publishing box after box must
    // not grow every node's table without bound.
    internal const int MaxInactiveBoxesPerAuthorAndKind = 10;

    /// <summary>
    /// Writes one of this node's OWN recovery events into its tables, right after the publisher logged
    /// it. A node never pulls its own events back through <see cref="ApplyAsync"/> (it is not in its own
    /// whitelist), and the rows must follow the same rules either way — so the publisher comes through
    /// here instead of writing the tables itself.
    /// </summary>
    public Task ApplyOwnRecoveryEventAsync(SyncEvent evt) => evt.EventType switch
    {
        EventTypes.RecoveryBoxSet => ApplyRecoveryBoxSetAsync(evt),
        EventTypes.RecoveryBoxRetire => ApplyRecoveryBoxRetireAsync(evt),
        EventTypes.RetiredLinkSet => ApplyRetiredLinkSetAsync(evt),
        EventTypes.StateAnchor => ApplyStateAnchorAsync(evt),
        EventTypes.SealedSecretSet => ApplySealedSecretSetAsync(evt),
        _ => throw new ArgumentException($"{evt.EventType} is not a recovery event.", nameof(evt)),
    };

    /// <summary>
    /// Clears the key material of every box that is not active here, and removes their logged events
    /// (<see cref="Recovery.RecoveryBoxMaterialPurge"/>). Called once the event that changed a box's state
    /// is in the log.
    /// </summary>
    public virtual Task PurgeInactiveBoxMaterialAsync() => Recovery.RecoveryBoxMaterialPurge.RunAsync(connFactory);

    private async Task ApplyRecoveryBoxSetAsync(SyncEvent evt)
    {
        var p = Deserialize<RecoveryBoxSetPayload>(evt.Payload);

        // AnyPeer event, so the whitelist gate lets every full node through; the one thing that keeps
        // a phone from overwriting the PC's strong box is that a box is always the SIGNER's own.
        if (!Guid.TryParseExact(p.AuthorNodeId, "D", out var author) || author != evt.NodeId)
            throw new UnauthorizedAccessException(
                $"recovery_box_set {evt.EventId}: node {evt.NodeId} may only publish its own box, not '{p.AuthorNodeId}'.");

        if (!Guid.TryParseExact(p.BoxId, "D", out var boxId))
            throw new InvalidDataException($"recovery_box_set {evt.EventId}: bad box id.");
        if (!RecoveryBoxKdf.IsAllowed(p.Kind, p.KdfPreset))
            throw new InvalidDataException(
                $"recovery_box_set {evt.EventId}: preset '{p.KdfPreset}' is not allowed for a {p.Kind} box.");
        if (!HexSha256.IsMatch(p.DekFingerprint ?? ""))
            throw new InvalidDataException($"recovery_box_set {evt.EventId}: bad DEK fingerprint.");

        var salt = FromBase64OrNull(p.Salt);
        var wrapped = FromBase64OrNull(p.Wrapped);
        var iv = FromBase64OrNull(p.Iv);
        if (!RecoveryBoxKdf.IsWellFormed(p.Kind, salt, wrapped, iv))
            throw new InvalidDataException($"recovery_box_set {evt.EventId}: malformed box material.");

        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);

        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            // A box id is written once. A redelivery is a no-op, and nothing — not even the author —
            // can bring a retired or superseded box back by sending its event again.
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_recovery_box WHERE box_id = @BoxId COLLATE NOCASE",
                new { BoxId = Id(boxId) }, tx);
            if (exists > 0)
            {
                tx.Commit();
                return;
            }

            // One active box per (author, kind): the highest version wins, whatever order the events
            // arrive in. A late older box is stored already superseded; a newer one supersedes the rest.
            var rivals = await conn.QueryAsync<(long LamportTs, string? SourceNodeId)>(
                @"SELECT lamport_ts, source_node_id FROM tbl_recovery_box
                  WHERE author_node_id = @Author COLLATE NOCASE AND kind = @Kind",
                new { Author = Id(author), Kind = p.Kind }, tx);
            var superseded = rivals.Any(r => ConflictResolver.IncomingWins(
                existing: incoming, incoming: new RowVersion(r.LamportTs, ParseNodeId(r.SourceNodeId))));

            if (!superseded)
            {
                await conn.ExecuteAsync(
                    @"UPDATE tbl_recovery_box SET status = 'R'
                      WHERE author_node_id = @Author COLLATE NOCASE AND kind = @Kind AND status = 'A'",
                    new { Author = Id(author), Kind = p.Kind }, tx);
            }

            await conn.ExecuteAsync(
                @"INSERT INTO tbl_recovery_box
                    (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv,
                     created_at, status, retired_by_box_id, lamport_ts, source_node_id)
                  VALUES (@BoxId, @Kind, @Author, @Fingerprint, @Epoch, @Preset, @Salt, @Wrapped, @Iv,
                     @CreatedAt, @Status, NULL, @Lamport, @Source)",
                new
                {
                    BoxId = Id(boxId), p.Kind, Author = Id(author), Fingerprint = p.DekFingerprint, Epoch = p.EpochHint,
                    Preset = p.KdfPreset, Salt = salt, Wrapped = wrapped, Iv = iv,
                    CreatedAt = evt.CreatedAt.ToUniversalTime().ToString("O"),
                    Status = superseded ? "R" : "A", Lamport = evt.LamportTs, Source = Id(evt.NodeId)
                }, tx);

            // Only the newest inactive rows stay. Everything deleted here is older than at least ten
            // rows of the same author and kind, so if its event ever comes back it is stored superseded
            // (and trimmed again) — deleting it cannot bring it back as the active box.
            var inactive = (await conn.QueryAsync<(string BoxId, long LamportTs, string? SourceNodeId)>(
                @"SELECT box_id, lamport_ts, source_node_id FROM tbl_recovery_box
                  WHERE author_node_id = @Author COLLATE NOCASE AND kind = @Kind AND status = 'R'",
                new { Author = Id(author), Kind = p.Kind }, tx)).ToList();
            inactive.Sort((x, y) => ConflictResolver.IncomingWins(
                existing: new RowVersion(x.LamportTs, ParseNodeId(x.SourceNodeId)),
                incoming: new RowVersion(y.LamportTs, ParseNodeId(y.SourceNodeId))) ? 1 : -1);
            foreach (var old in inactive.Skip(MaxInactiveBoxesPerAuthorAndKind))
                await conn.ExecuteAsync("DELETE FROM tbl_recovery_box WHERE box_id = @BoxId", new { old.BoxId }, tx);

            tx.Commit();
        }

        // The box that just arrived may be the covering box, or the target, a waiting retire needs.
        await ApplyPendingRetiresAsync();
    }

    private async Task ApplyRecoveryBoxRetireAsync(SyncEvent evt)
    {
        var p = Deserialize<RecoveryBoxRetirePayload>(evt.Payload);

        if (!Guid.TryParseExact(p.CoveringBoxId, "D", out var covering))
            throw new InvalidDataException($"recovery_box_retire {evt.EventId}: bad covering box id.");
        if (p.BoxIds is not { Count: > 0 and <= MaxRetireTargets })
            throw new InvalidDataException($"recovery_box_retire {evt.EventId}: needs 1..{MaxRetireTargets} box ids.");

        var targets = new List<Guid>();
        foreach (var id in p.BoxIds)
        {
            if (!Guid.TryParseExact(id, "D", out var target))
                throw new InvalidDataException($"recovery_box_retire {evt.EventId}: bad box id '{id}'.");
            // Retiring the covering box itself would be "cleanup" that leaves no box for the key.
            if (target != covering) targets.Add(target);
        }

        // Every target goes through the waiting list, applied or not: the rule "only while the
        // covering box is active here, otherwise wait" then lives in exactly one place.
        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var target in targets.Distinct())
            {
                await conn.ExecuteAsync(
                    @"INSERT OR IGNORE INTO tbl_recovery_box_pending_retire
                        (event_id, box_id, covering_box_id, lamport_ts, source_node_id, received_at)
                      VALUES (@EventId, @BoxId, @Covering, @Lamport, @Source, @Now)",
                    new
                    {
                        EventId = Id(evt.EventId), BoxId = Id(target), Covering = Id(covering),
                        Lamport = evt.LamportTs, Source = Id(evt.NodeId), Now = DateTime.UtcNow.ToString("O")
                    }, tx);
            }
            tx.Commit();
        }

        await ApplyPendingRetiresAsync();
    }

    /// <summary>
    /// Applies every waiting retire whose covering box is an ACTIVE strong box here and whose target
    /// has arrived. A retire whose covering box holds a different key than the target is refused for
    /// good: it would remove the last way to that key, which is exactly what a retire must never do.
    /// Everything else keeps waiting — never dropped (plan 6.3).
    /// </summary>
    private async Task ApplyPendingRetiresAsync()
    {
        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var ready = (await conn.QueryAsync<(string EventId, string BoxId, string CoveringBoxId, string TargetFingerprint, string CoveringFingerprint)>(
            @"SELECT p.event_id, p.box_id, p.covering_box_id, t.dek_fingerprint, c.dek_fingerprint
              FROM tbl_recovery_box_pending_retire p
              JOIN tbl_recovery_box c ON c.box_id = p.covering_box_id COLLATE NOCASE
                                     AND c.kind = 'strong' AND c.status = 'A'
              JOIN tbl_recovery_box t ON t.box_id = p.box_id COLLATE NOCASE",
            transaction: tx)).ToList();

        foreach (var r in ready)
        {
            if (string.Equals(r.TargetFingerprint, r.CoveringFingerprint, StringComparison.Ordinal))
            {
                await conn.ExecuteAsync(
                    @"UPDATE tbl_recovery_box SET status = 'R', retired_by_box_id = @Covering
                      WHERE box_id = @BoxId COLLATE NOCASE AND status = 'A'",
                    new { Covering = r.CoveringBoxId, r.BoxId }, tx);
            }
            else
            {
                logger.LogWarning(
                    "recovery_box_retire {EventId} refused for box {BoxId}: covering box {Covering} holds a different key",
                    r.EventId, r.BoxId, r.CoveringBoxId);
            }

            await conn.ExecuteAsync(
                "DELETE FROM tbl_recovery_box_pending_retire WHERE event_id = @EventId AND box_id = @BoxId",
                new { r.EventId, r.BoxId }, tx);
        }

        tx.Commit();
    }

    private async Task ApplyRetiredLinkSetAsync(SyncEvent evt)
    {
        var p = Deserialize<RetiredLinkSetPayload>(evt.Payload);

        if (!Guid.TryParseExact(p.CommitId, "D", out var commitId))
            throw new InvalidDataException($"retired_link_set {evt.EventId}: bad commit id.");
        if (!HexSha256.IsMatch(p.OldFingerprint ?? "") || !HexSha256.IsMatch(p.NewFingerprint ?? "")
            || p.OldFingerprint == p.NewFingerprint)
            throw new InvalidDataException($"retired_link_set {evt.EventId}: bad fingerprints.");

        // The old DEK wrapped under the new one, in the key-slot wrap format.
        var wrapped = FromBase64OrNull(p.Wrapped);
        var iv = FromBase64OrNull(p.Iv);
        if (!RecoveryBoxKdf.IsWellFormed(new byte[RecoveryBoxKdf.SaltSize], wrapped, iv))
            throw new InvalidDataException($"retired_link_set {evt.EventId}: malformed link material.");

        // Keyed by (commit, author): a forged link published first cannot shadow the real one, and a
        // restore tries every link it has. INSERT OR IGNORE — a link is immutable once written.
        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_dek_retired_link
                (commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv, created_at)
              VALUES (@CommitId, @Author, @Old, @New, @Wrapped, @Iv, @CreatedAt)",
            new
            {
                CommitId = Id(commitId), Author = Id(evt.NodeId), Old = p.OldFingerprint, New = p.NewFingerprint,
                Wrapped = wrapped, Iv = iv, CreatedAt = evt.CreatedAt.ToUniversalTime().ToString("O")
            });
    }

    private async Task ApplyStateAnchorAsync(SyncEvent evt)
    {
        // SuperadminOnly is enforced by ApplyAsync before this runs.
        var p = Deserialize<StateAnchorPayload>(evt.Payload);

        if (!Guid.TryParseExact(p.AnchorId, "D", out var anchorId))
            throw new InvalidDataException($"state_anchor {evt.EventId}: bad anchor id.");
        if (!HexSha256.IsMatch(p.DekFingerprint ?? "") || !StateDigestValue.IsMatch(p.Digest ?? "") || !HexSha256.IsMatch(p.Hmac ?? ""))
            throw new InvalidDataException($"state_anchor {evt.EventId}: bad fingerprint, digest or hmac.");
        if (!DateTime.TryParse(p.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new InvalidDataException($"state_anchor {evt.EventId}: bad created_at.");
        if (p.PositionVector is not { Count: <= MaxAnchorVectorEntries }
            || p.PositionVector.Any(kv => !Guid.TryParseExact(kv.Key, "D", out _) || kv.Value < 0))
            throw new InvalidDataException($"state_anchor {evt.EventId}: bad position vector.");

        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_state_anchor
                (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
              VALUES (@AnchorId, @Author, @Fingerprint, @Vector, @Digest, @Hmac, @CreatedAt, @Lamport)",
            new
            {
                AnchorId = Id(anchorId), Author = Id(evt.NodeId), Fingerprint = p.DekFingerprint,
                Vector = JsonSerializer.Serialize(new SortedDictionary<string, long>(
                    p.PositionVector.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)),
                p.Digest, p.Hmac, p.CreatedAt, Lamport = evt.LamportTs
            });
    }

    private async Task ApplySealedSecretSetAsync(SyncEvent evt)
    {
        var p = Deserialize<SealedSecretSetPayload>(evt.Payload);

        if (!SealedSecretName.IsMatch(p.Name ?? ""))
            throw new InvalidDataException($"sealed_secret_set {evt.EventId}: bad secret name '{p.Name}'.");
        if (!HexSha256.IsMatch(p.DekFingerprint ?? ""))
            throw new InvalidDataException($"sealed_secret_set {evt.EventId}: bad DEK fingerprint.");
        var wrapped = FromBase64OrNull(p.Wrapped);
        var iv = FromBase64OrNull(p.Iv);
        if (wrapped is not { Length: > CryptoConstants.TagSize and <= MaxSealedSecretBytes } || iv is not { Length: CryptoConstants.IvSize })
            throw new InvalidDataException($"sealed_secret_set {evt.EventId}: malformed sealed value.");

        var name = p.Name!.ToLowerInvariant();
        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);

        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Plain LWW per name: after a rotation any full node re-seals under the new DEK, and the newer
        // seal replaces the older one everywhere regardless of arrival order.
        var existing = await conn.QuerySingleOrDefaultAsync<(long LamportTs, string? SourceNodeId)?>(
            "SELECT lamport_ts, source_node_id FROM tbl_sealed_secret WHERE name = @Name",
            new { Name = name }, tx);
        if (existing is { } e && !ConflictResolver.IncomingWins(new RowVersion(e.LamportTs, ParseNodeId(e.SourceNodeId)), incoming))
        {
            tx.Commit();
            return;
        }

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts, source_node_id)
              VALUES (@Name, @Fingerprint, @Wrapped, @Iv, @UpdatedAt, 'A', @Lamport, @Source)
              ON CONFLICT(name) DO UPDATE SET dek_fingerprint = excluded.dek_fingerprint, wrapped = excluded.wrapped,
                iv = excluded.iv, updated_at = excluded.updated_at, status = 'A',
                lamport_ts = excluded.lamport_ts, source_node_id = excluded.source_node_id",
            new
            {
                Name = name, Fingerprint = p.DekFingerprint, Wrapped = wrapped, Iv = iv,
                UpdatedAt = evt.CreatedAt.ToUniversalTime().ToString("O"), Lamport = evt.LamportTs, Source = Id(evt.NodeId)
            }, tx);
        tx.Commit();
    }

    // Ids are written as lowercase canonical strings, never through the host's Guid type handler: the
    // rows replicate, and every node must store (and snapshot) the identical text.
    private static string Id(Guid value) => value.ToString("D");

    private static byte[]? FromBase64OrNull(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }

    // Rows written before a source was known compare as Guid.Empty, the same reading every other
    // replicated table gives a missing source.
    private static Guid ParseNodeId(string? value) =>
        Guid.TryParse(value, out var g) ? g : Guid.Empty;
}
