using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class BlindInviteRepository(DbConnectionFactory factory) : BaseRepository(factory), IBlindInviteRepository
{
    private const string Columns =
        @"invite_id AS InviteId, secret_hash AS SecretHash, label AS Label, source_name AS SourceName, address AS Address, trust AS Trust, pin AS Pin,
          created_by AS CreatedBy, created_at AS CreatedAt, expires_at AS ExpiresAt, state AS State, redeemed_at AS RedeemedAt,
          redeemer_node_id AS RedeemerNodeId, redeemer_key AS RedeemerKey, redeemer_name AS RedeemerName, redeemer_platform AS RedeemerPlatform,
          redeemer_version AS RedeemerVersion, redeemer_seen_from AS RedeemerSeenFrom, backup_key_enc AS BackupKeyEnc,
          kind AS Kind, pair_code_enc AS PairCodeEnc, confirm_deadline AS ConfirmDeadline, confirming_at AS ConfirmingAt, enroll_event_id AS EnrollEventId,
          decided_at AS DecidedAt, decided_by AS DecidedBy";

    // What counts as open: the one definition, used by the cap and by the count.
    private const string OpenWhere =
        "(state = 'issued' AND expires_at > @now) OR (state IN ('redeemed', 'confirming') AND confirm_deadline > @now)";

    public async Task<bool> TryInsertAsync(BlindInviteRecord row, DateTime now, int maxOpen)
    {
        using var conn = OpenConnection();
        // The count is part of the INSERT ... SELECT: SQLite takes the write lock for the whole statement, so two owners cannot both count 19 and both insert.
        return await conn.ExecuteAsync(
            $@"INSERT INTO tbl_blind_invite
                (invite_id, secret_hash, label, source_name, address, trust, pin, created_by, created_at, expires_at, state)
              SELECT @InviteId, @SecretHash, @Label, @SourceName, @Address, @Trust, @Pin, @CreatedBy, @CreatedAt, @ExpiresAt, @State
              WHERE (SELECT COUNT(*) FROM tbl_blind_invite WHERE {OpenWhere}) < @maxOpen",
            new
            {
                row.InviteId, row.SecretHash, row.Label, row.SourceName, row.Address, row.Trust, row.Pin, row.CreatedBy, row.CreatedAt, row.ExpiresAt, row.State,
                now, maxOpen
            }) == 1;
    }

    public async Task<BlindInviteRecord?> GetAsync(string inviteId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<BlindInviteRecord>(
            $"SELECT {Columns} FROM tbl_blind_invite WHERE invite_id = @inviteId", new { inviteId });
    }

    public async Task<IReadOnlyList<BlindInviteRecord>> ListAsync(int limit = 200)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<BlindInviteRecord>(
            $"SELECT {Columns} FROM tbl_blind_invite ORDER BY created_at DESC LIMIT @limit", new { limit })).ToList();
    }

    public async Task<int> CountOpenAsync(DateTime now)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM tbl_blind_invite WHERE {OpenWhere}", new { now });
    }

    public async Task<bool> TryRedeemAsync(string inviteId, string secretHash, DateTime now, BlindInviteRedemption r)
    {
        using var conn = OpenConnection();
        // One statement decides: the first caller changes the row, every other one finds it no longer 'issued' and changes nothing.
        var changed = await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite
                 SET state = 'redeemed', redeemed_at = @now, redeemer_node_id = @NodeId, redeemer_key = @PublicKey, redeemer_name = @DeviceName,
                     redeemer_platform = @Platform, redeemer_version = @AppVersion, redeemer_seen_from = @SeenFrom,
                     backup_key_enc = @BackupKeyEnc, kind = @Kind, pair_code_enc = @PairCodeEnc, confirm_deadline = @ConfirmDeadline
               WHERE invite_id = @inviteId AND state = 'issued' AND expires_at > @now AND secret_hash = @secretHash",
            new { inviteId, secretHash, now, NodeId = r.NodeId.ToString("D"), r.PublicKey, r.DeviceName, r.Platform, r.AppVersion, r.SeenFrom, r.BackupKeyEnc, r.Kind, r.PairCodeEnc, r.ConfirmDeadline });
        return changed == 1;
    }

    public async Task<bool> TryReplacePairCodeAsync(string inviteId, Guid nodeId, byte[] pairCodeEnc)
    {
        using var conn = OpenConnection();
        // Only the server node that redeemed it, and only while the owner has not started to confirm: a retry of the redemption with a fresh pair code (the first answer was lost and the
        // node began again) must not leave the hub holding a code the node no longer has.
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET pair_code_enc = @pairCodeEnc
               WHERE invite_id = @inviteId AND state = 'redeemed' AND kind = 'server' AND redeemer_node_id = @nodeId",
            new { inviteId, pairCodeEnc, nodeId = nodeId.ToString("D") }) == 1;
    }

    public async Task<bool> HasOpenServerRedemptionAsync(Guid nodeId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_blind_invite
               WHERE kind = 'server' AND state IN ('redeemed', 'confirming') AND redeemer_node_id = @nodeId COLLATE NOCASE",
            new { nodeId = nodeId.ToString("D") }) > 0;
    }

    public async Task<bool> TryBeginConfirmAsync(string inviteId, DateTime now, string enrollEventId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET state = 'confirming', confirming_at = @now, enroll_event_id = @enrollEventId
               WHERE invite_id = @inviteId AND state = 'redeemed' AND confirm_deadline > @now",
            new { inviteId, now, enrollEventId }) == 1;
    }

    public async Task<bool> TryConfirmAsync(string inviteId, DateTime now, string decidedBy)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET state = 'confirmed', decided_at = @now, decided_by = @decidedBy
               WHERE invite_id = @inviteId AND state = 'confirming' AND confirm_deadline > @now",
            new { inviteId, now, decidedBy }) == 1;
    }

    public async Task<bool> TryAbortConfirmingAsync(string inviteId, string finalState, DateTime now, string decidedBy)
    {
        if (finalState is not (BlindInviteRowState.Refused or BlindInviteRowState.Expired))
            throw new ArgumentException("An enrolment that did not complete ends refused or expired.", nameof(finalState));
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET state = @finalState, decided_at = @now, decided_by = @decidedBy
               WHERE invite_id = @inviteId AND state = 'confirming'",
            new { inviteId, finalState, now, decidedBy }) == 1;
    }

    public async Task<IReadOnlyList<BlindInviteRecord>> ListConfirmingAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<BlindInviteRecord>(
            $"SELECT {Columns} FROM tbl_blind_invite WHERE state = 'confirming' ORDER BY confirming_at")).ToList();
    }

    public async Task<bool> TryRefuseAsync(string inviteId, DateTime now, string decidedBy)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET state = 'refused', decided_at = @now, decided_by = @decidedBy
               WHERE invite_id = @inviteId AND state = 'redeemed'",
            new { inviteId, now, decidedBy }) == 1;
    }

    public async Task<bool> TryWithdrawAsync(string inviteId, DateTime now, string decidedBy)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET state = 'expired', decided_at = @now, decided_by = @decidedBy
               WHERE invite_id = @inviteId AND state = 'issued'",
            new { inviteId, now, decidedBy }) == 1;
    }

    public async Task<IReadOnlyList<string>> ListDueForExpiryAsync(DateTime now)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<string>(
            @"SELECT invite_id FROM tbl_blind_invite
              WHERE (state = 'issued' AND expires_at <= @now) OR (state = 'redeemed' AND confirm_deadline <= @now)",
            new { now })).ToList();
    }

    public async Task<bool> TryExpireAsync(string inviteId, DateTime now)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_blind_invite SET state = 'expired', decided_at = @now
               WHERE invite_id = @inviteId
                 AND ((state = 'issued' AND expires_at <= @now) OR (state = 'redeemed' AND confirm_deadline <= @now))",
            new { inviteId, now }) == 1;
    }

    public async Task<int> DeleteFinalBeforeAsync(DateTime cutoff)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"DELETE FROM tbl_blind_invite
              WHERE state IN ('confirmed', 'refused', 'expired') AND COALESCE(decided_at, expires_at) < @cutoff",
            new { cutoff });
    }
}
