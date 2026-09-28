using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;
using Dapper;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>What the recovery material on this node looks like, for the admin UI and the blind status page.</summary>
public sealed record RecoveryStatus(
    int ActiveBoxes,
    int StrongBoxes,
    int DeviceBoxes,
    string? CurrentKeyFingerprint,
    bool CurrentKeyCovered,
    bool CurrentKeyHasStrongBox,
    IReadOnlyList<string> DevicesWithOtherPassword);

/// <summary>The newest integrity anchor this node holds.</summary>
public sealed record AnchorStatus(string AnchorId, string CreatedAt, string DekFingerprint, string AuthorNodeId);

/// <summary>
/// Reads the state of the recovery boxes (plan 5.6, 6.3, 6.5 warnings; status fields of CONTRACTS §5).
/// On a full node "the current key" is the session DEK; a blind node has no DEK, so there it is the key
/// the newest anchor vouches for.
/// </summary>
public class RecoveryStatusService(
    RecoveryBoxQueries queries,
    SessionService session,
    IWhitelistRepository whitelist,
    IDbConnectionFactory connFactory)
{
    /// <param name="slotId">The signed-in superadmin's key slot: whose password the cleanup tried.</param>
    public async Task<RecoveryStatus> GetAsync(int? slotId)
    {
        var boxes = await queries.ActiveBoxesAsync();
        var current = await CurrentFingerprintAsync();

        var otherPassword = new List<string>();
        if (slotId is { } slot)
        {
            foreach (var box in boxes.Where(b => b.Kind == RecoveryBoxKdf.KindDevice))
            {
                if (await queries.CheckedAsync(box.BoxId, slot) is { Opens: false })
                    otherPassword.Add(await DisplayNameAsync(box.AuthorNodeId));
            }
        }

        return new RecoveryStatus(
            ActiveBoxes: boxes.Count,
            StrongBoxes: boxes.Count(b => b.Kind == RecoveryBoxKdf.KindStrong),
            DeviceBoxes: boxes.Count(b => b.Kind == RecoveryBoxKdf.KindDevice),
            CurrentKeyFingerprint: current,
            CurrentKeyCovered: current != null && boxes.Any(b => b.DekFingerprint == current),
            CurrentKeyHasStrongBox: current != null && boxes.Any(b => b.DekFingerprint == current && b.Kind == RecoveryBoxKdf.KindStrong),
            DevicesWithOtherPassword: otherPassword.Distinct().ToList());
    }

    public async Task<AnchorStatus?> NewestAnchorAsync()
    {
        using var conn = connFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AnchorStatus>(
            @"SELECT anchor_id AS AnchorId, created_at AS CreatedAt, dek_fingerprint AS DekFingerprint,
                     author_node_id AS AuthorNodeId
              FROM tbl_state_anchor ORDER BY created_at DESC, lamport_ts DESC LIMIT 1");
    }

    /// <summary>
    /// The newest anchor and whether this node's rows still reproduce its digest at the anchor's cut.
    /// Needs no DEK — the digest is over ids, versions and ciphertext hashes — so a blind node can say
    /// "my copy is intact as of this date" (it cannot check the anchor's MAC; a restore does that).
    /// </summary>
    public async Task<(AnchorStatus Anchor, bool StateMatches)?> NewestAnchorStateAsync()
    {
        using var conn = connFactory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(string AnchorId, string CreatedAt, string Fp, string Author, string Vector, string Digest)?>(
            @"SELECT anchor_id, created_at, dek_fingerprint, author_node_id, position_vector, digest
              FROM tbl_state_anchor ORDER BY created_at DESC, lamport_ts DESC LIMIT 1");
        if (row is not { } a) return null;

        var vector = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(a.Vector) ?? [];
        var cut = vector.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value, StringComparer.Ordinal);
        var state = await StateDigest.ComputeAsync(conn, cut);
        return (new AnchorStatus(a.AnchorId, a.CreatedAt, a.Fp, a.Author), StateDigest.Matches(a.Digest, state));
    }

    private async Task<string?> CurrentFingerprintAsync()
    {
        if (session.IsUnlocked)
        {
            var dek = session.GetMasterDek();
            try { return DekFingerprint.Of(dek); }
            finally { Array.Clear(dek); }
        }
        return (await NewestAnchorAsync())?.DekFingerprint;
    }

    private async Task<string> DisplayNameAsync(string nodeId)
    {
        if (Guid.TryParse(nodeId, out var id)
            && await whitelist.GetByNodeIdAsync(id, includeDeleted: true) is { } row
            && !string.IsNullOrWhiteSpace(row.DisplayName))
            return row.DisplayName;
        return nodeId;
    }
}
