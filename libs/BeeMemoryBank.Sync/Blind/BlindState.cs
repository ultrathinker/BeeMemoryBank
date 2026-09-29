using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// A blind node's own flags (tbl_blind_state, migration 028). Local only — never replicated.
/// </summary>
public sealed class BlindState(IDbConnectionFactory factory)
{
    private const string ReseedNeededKey = "reseed_needed";
    private const string PairingSecretKey = "pairing_secret";
    private const string PairingExpiresKey = "pairing_expires_at";
    private const string AdoptedCheckpointPrefix = "adopted_checkpoint:";

    /// <summary>
    /// Set by <see cref="StoredEventRepair"/> on ANY node (the table exists on every node) that found
    /// a rotation event sealing the master DEK for a blind node in its log: the DEK must be treated
    /// as exposed and rotated. Reported by /api/sync/my-standing; nothing clears it but an operator.
    /// </summary>
    public const string DekExposureKey = "dek_exposure";

    public Task<string?> GetDekExposureAsync() => GetAsync(DekExposureKey);

    /// <summary>
    /// Plan 5.3: a restore_network replaced the mesh's state, and this node — which cannot join a
    /// network restore without the DEK — must be reseeded by the first superadmin full node.
    /// The value says why, for the console and the PC. A reseed clears it by replacing the database.
    /// </summary>
    public Task SetReseedNeededAsync(string reason) => SetAsync(ReseedNeededKey, reason);

    public Task<string?> GetReseedNeededAsync() => GetAsync(ReseedNeededKey);

    /// <summary>
    /// The one-time secret of the current pair code (plan 4.1). Stored as issued: the console shows
    /// the code again until it expires, and the value is worthless after 15 minutes or one seed.
    /// </summary>
    public async Task SetPairingSecretAsync(string secret, DateTime expiresAt)
    {
        await SetAsync(PairingSecretKey, secret);
        await SetAsync(PairingExpiresKey, expiresAt.ToString("O"));
    }

    public async Task ClearPairingSecretAsync()
    {
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key IN (@PairingSecretKey, @PairingExpiresKey)",
            new { PairingSecretKey, PairingExpiresKey });
    }

    public async Task<(string Secret, DateTime ExpiresAt)?> GetPairingSecretAsync()
    {
        var secret = await GetAsync(PairingSecretKey);
        var expires = await GetAsync(PairingExpiresKey);
        if (secret is null || expires is null) return null;
        return (secret, DateTime.Parse(expires, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// On a FULL node: the checkpoint it took from blind peer <paramref name="blindNodeId"/> as its pull position
    /// (<see cref="SyncClient.SyncWithPeerAsync"/>), for the Blind nodes page to show as "adopted checkpoint N". Only a
    /// note for the owner: the pull position itself is what stops a second adoption.
    /// </summary>
    public Task SetAdoptedCheckpointAsync(Guid blindNodeId, long checkpoint) =>
        SetAsync(AdoptedCheckpointKey(blindNodeId), checkpoint.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A new pairing or a reseed starts from a known position again, so the note about an earlier adoption goes.</summary>
    public async Task ClearAdoptedCheckpointAsync(Guid blindNodeId)
    {
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = @key", new { key = AdoptedCheckpointKey(blindNodeId) });
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetAdoptedCheckpointsAsync()
    {
        using var conn = factory.CreateConnection();
        var rows = await conn.QueryAsync<StateRow>(
            "SELECT key AS Key, value AS Value FROM tbl_blind_state WHERE substr(key, 1, @len) = @prefix",
            new { len = AdoptedCheckpointPrefix.Length, prefix = AdoptedCheckpointPrefix });
        var result = new Dictionary<Guid, long>();
        foreach (var row in rows)
            if (Guid.TryParse(row.Key[AdoptedCheckpointPrefix.Length..], out var id)
                && long.TryParse(row.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cp))
                result[id] = cp;
        return result;
    }

    private sealed class StateRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private static string AdoptedCheckpointKey(Guid blindNodeId) => AdoptedCheckpointPrefix + blindNodeId.ToString("D");

    private async Task SetAsync(string key, string value)
    {
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@key, @value, @now)
              ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            new { key, value, now = DateTime.UtcNow.ToString("O") });
    }

    private async Task<string?> GetAsync(string key)
    {
        using var conn = factory.CreateConnection();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT value FROM tbl_blind_state WHERE key = @key", new { key });
    }
}
