using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>A row of <c>tbl_recovery_box</c> as the Api reads it.</summary>
public sealed record RecoveryBoxRow(
    string BoxId, string Kind, string AuthorNodeId, string DekFingerprint, long EpochHint, string KdfPreset,
    byte[] Salt, byte[] Wrapped, byte[] Iv, string CreatedAt, string Status, string? RetiredByBoxId,
    long LamportTs, string? SourceNodeId);

/// <summary>Read side of the replicated recovery tables (migration 027) for the Api's recovery services.</summary>
public class RecoveryBoxQueries(IDbConnectionFactory connFactory)
{
    private const string BoxColumns =
        @"box_id AS BoxId, kind AS Kind, author_node_id AS AuthorNodeId, dek_fingerprint AS DekFingerprint,
          epoch_hint AS EpochHint, kdf_preset AS KdfPreset, salt AS Salt, wrapped AS Wrapped, iv AS Iv,
          created_at AS CreatedAt, status AS Status, retired_by_box_id AS RetiredByBoxId,
          lamport_ts AS LamportTs, source_node_id AS SourceNodeId";

    public async Task<List<RecoveryBoxRow>> ActiveBoxesAsync()
    {
        using var conn = connFactory.CreateConnection();
        return (await conn.QueryAsync<RecoveryBoxRow>(
            $"SELECT {BoxColumns} FROM tbl_recovery_box WHERE status = 'A' ORDER BY kind DESC, epoch_hint DESC, lamport_ts DESC")).ToList();
    }

    /// <summary>This node's active strong box, if it holds <paramref name="fingerprint"/>.</summary>
    public async Task<RecoveryBoxRow?> OwnStrongBoxAsync(Guid nodeId, string fingerprint)
    {
        using var conn = connFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RecoveryBoxRow>(
            $@"SELECT {BoxColumns} FROM tbl_recovery_box
               WHERE author_node_id = @Node COLLATE NOCASE AND kind = 'strong' AND status = 'A' AND dek_fingerprint = @Fp",
            new { Node = nodeId, Fp = fingerprint });
    }

    /// <summary>Remembered answer of "does this slot's password open that box" (migration 030).</summary>
    public async Task<(bool Opens, string? Fingerprint)?> CheckedAsync(string boxId, int slotId)
    {
        using var conn = connFactory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(long Opens, string? Fp)?>(
            "SELECT opens, dek_fingerprint FROM tbl_recovery_box_check WHERE box_id = @B COLLATE NOCASE AND slot_id = @S",
            new { B = boxId, S = slotId });
        return row is { } r ? (r.Opens != 0, r.Fp) : null;
    }

    public async Task RememberCheckAsync(string boxId, int slotId, bool opens, string? fingerprint)
    {
        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR REPLACE INTO tbl_recovery_box_check (box_id, slot_id, opens, dek_fingerprint, checked_at)
              VALUES (@B, @S, @O, @F, @T)",
            new { B = boxId.ToLowerInvariant(), S = slotId, O = opens ? 1 : 0, F = fingerprint, T = DateTime.UtcNow.ToString("O") });
    }
}
