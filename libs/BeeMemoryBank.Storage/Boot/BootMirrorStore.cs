using Dapper;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// The identity and whitelist mirrors the relay authenticates with while the database is closed (docs/design/step-b-spec.md 2.2, 6.4).
/// Read here; replaced as a whole by <see cref="BootStoreWriter"/> at every rewrite. The only change applied between rewrites is
/// <see cref="SuspendAsync"/>.
/// </summary>
public sealed class BootMirrorStore : IBootMirrorStore
{
    private readonly BootStoreConnectionFactory _factory;

    public BootMirrorStore(BootStoreConnectionFactory factory)
    {
        DapperConfig.Configure();
        _factory = factory;
    }

    public async Task<BootIdentity?> GetIdentityAsync(CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        var row = await connection.QuerySingleOrDefaultAsync<(string NodeId, string DisplayName, byte[] Key)?>(
            "SELECT node_id, display_name, ed25519_public_key FROM tbl_boot_identity LIMIT 1");
        return row is { } r && Guid.TryParse(r.NodeId, out var id) ? new BootIdentity(id, r.DisplayName, r.Key) : null;
    }

    public async Task<(byte[] PublicKey, bool IsSuperadmin)?> GetActivePeerAsync(Guid nodeId, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        var row = await connection.QuerySingleOrDefaultAsync<(byte[] Key, long Superadmin)?>(
            "SELECT ed25519_public_key, is_superadmin FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'",
            new { nodeId = nodeId.ToString("D") });
        if (row is not { } r) return null;
        // A blind node never carries authority, whatever its row says (the rule WhitelistRepository applies to every read).
        return (r.Key, r.Superadmin != 0 && !BlindNodeId.IsBlind(nodeId));
    }

    public async Task SuspendAsync(Guid nodeId, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        await connection.ExecuteAsync(
            "UPDATE tbl_whitelist SET status = 'S', updated_at = @now WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'",
            new { nodeId = nodeId.ToString("D"), now = DateTime.UtcNow.ToString("o") });
    }
}
