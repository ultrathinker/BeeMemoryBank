using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Boot;

/// <summary>
/// Migration 040 (docs/design/step-b-spec.md 4.3, Appendix B.2): <c>unlock_material_version</c> moves inside the writing transaction
/// for every change of the unlock material, and does not move for the columns that change on every request.
/// </summary>
public class UnlockMaterialVersionTriggerTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory("bmb_umv_" + Guid.NewGuid().ToString("N"));
        await new MigrationRunner(_factory).RunMigrationsAsync();
        Execute("INSERT INTO tbl_user (username, display_name, password_hash, role, created_at) VALUES ('u', 'u', 'x', 'superadmin', 'now')");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private void Execute(string sql, object? args = null)
    {
        using var c = _factory.CreateConnection();
        c.Execute(sql, args);
    }

    private long Version()
    {
        using var c = _factory.CreateConnection();
        return c.ExecuteScalar<long>("SELECT CAST(value AS INTEGER) FROM tbl_migration_marker WHERE key = 'unlock_material_version'");
    }

    private void Moves(string what, string sql, object? args = null)
    {
        var before = Version();
        Execute(sql, args);
        Version().Should().BeGreaterThan(before, what + " moves the version");
    }

    private void StaysPut(string what, string sql, object? args = null)
    {
        var before = Version();
        Execute(sql, args);
        Version().Should().Be(before, what + " does not move the version");
    }

    [Fact]
    public void TheMarkerAndTheThirteenTriggersExistAfterTheMigration()
    {
        using var c = _factory.CreateConnection();
        c.ExecuteScalar<string>("SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'").Should().NotBeNull();
        c.Query<string>("SELECT name FROM sqlite_schema WHERE type = 'trigger' AND name LIKE 'trg_umv_%'").Should().HaveCount(13);
    }

    [Fact]
    public void KeySlotsMoveTheVersion_OnInsertUpdateAndDelete()
    {
        Moves("a slot insert", "INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at) VALUES ('user', @w, @iv, 'now')",
            new { w = RandomNumberGenerator.GetBytes(49), iv = RandomNumberGenerator.GetBytes(12) });
        Moves("a slot update", "UPDATE tbl_key_slot SET iv = @iv", new { iv = RandomNumberGenerator.GetBytes(12) });
        Moves("a slot delete", "DELETE FROM tbl_key_slot");
    }

    [Fact]
    public void AgentsMoveTheVersion_ForTheUnlockColumnsOnly()
    {
        Moves("an agent insert",
            @"INSERT INTO tbl_agent (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
              VALUES ('a', 'bee_', 'h', @w, @iv, 1, @salt, 'A', 'now', 1)",
            new { w = RandomNumberGenerator.GetBytes(49), iv = RandomNumberGenerator.GetBytes(12), salt = RandomNumberGenerator.GetBytes(16) });
        StaysPut("last_accessed_at", "UPDATE tbl_agent SET last_accessed_at = 'later'");
        StaysPut("request_count", "UPDATE tbl_agent SET request_count = request_count + 1");
        StaysPut("the name", "UPDATE tbl_agent SET name = 'renamed', description = 'd'");
        Moves("the wrapped DEK", "UPDATE tbl_agent SET encrypted_dek = NULL, dek_iv = NULL");
        Moves("the status", "UPDATE tbl_agent SET status = 'R'");
        Moves("the key hash", "UPDATE tbl_agent SET key_hash = 'h2'");
        Moves("the salt", "UPDATE tbl_agent SET salt = @salt", new { salt = RandomNumberGenerator.GetBytes(16) });
        Moves("an agent delete", "DELETE FROM tbl_agent");
    }

    [Fact]
    public void UsersMoveTheVersion_ForRoleActivityAndSlotOnly()
    {
        StaysPut("the display name", "UPDATE tbl_user SET display_name = 'x'");
        StaysPut("the last login", "UPDATE tbl_user SET last_login_at = 'now'");
        Moves("the role", "UPDATE tbl_user SET role = 'user'");
        Moves("is_active", "UPDATE tbl_user SET is_active = 0");
        Moves("key_slot_id", "UPDATE tbl_user SET key_slot_id = 5");
        Execute("INSERT INTO tbl_user (username, display_name, password_hash, role, created_at) VALUES ('v', 'v', 'x', 'user', 'now')");
        Moves("a user delete", "DELETE FROM tbl_user WHERE username = 'v'");
    }

    [Fact]
    public void TheWhitelistMovesTheVersion_ForWhatTheRelayAuthenticatesWithButNotForObservations()
    {
        Moves("a peer insert",
            "INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, created_at, updated_at) VALUES ('n1', 'p', @k, 'now', 'now')",
            new { k = RandomNumberGenerator.GetBytes(32) });
        StaysPut("the protocol observation", "UPDATE tbl_whitelist SET last_protocol_version = 4, last_protocol_seen_at = 'now'");
        StaysPut("the capability observation", "UPDATE tbl_whitelist SET last_capabilities = 'x', last_app_version = '2.6.0', last_capabilities_seen_at = 'now'");
        StaysPut("the lamport stamp", "UPDATE tbl_whitelist SET lamport_ts = 9, updated_at = 'later', source_node_id = 's'");
        Moves("the display name", "UPDATE tbl_whitelist SET display_name = 'q'");
        Moves("the address", "UPDATE tbl_whitelist SET api_address = 'https://x'");
        Moves("the status", "UPDATE tbl_whitelist SET status = 'R'");
        Moves("is_superadmin", "UPDATE tbl_whitelist SET is_superadmin = 1");
        Moves("deleted_at", "UPDATE tbl_whitelist SET deleted_at = 'now'");
        Moves("the TLS pin", "UPDATE tbl_whitelist SET tls_spki = 'pin'");
        Moves("the TLS trust", "UPDATE tbl_whitelist SET tls_trust = 'tofu'");
        Moves("the public key", "UPDATE tbl_whitelist SET ed25519_public_key = @k", new { k = RandomNumberGenerator.GetBytes(32) });
        Moves("a peer delete", "DELETE FROM tbl_whitelist");
    }

    [Fact]
    public void TheIdentityMovesTheVersion()
    {
        Moves("the identity insert",
            "INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, created_at) VALUES ('id1', 'n', @k, @p, 'now')",
            new { k = RandomNumberGenerator.GetBytes(32), p = RandomNumberGenerator.GetBytes(48) });
        StaysPut("a sentinel or epoch change", "UPDATE tbl_node_identity SET dek_epoch = dek_epoch + 1, initial_sync_completed = 1");
        Moves("the display name", "UPDATE tbl_node_identity SET display_name = 'renamed'");
        Moves("the public key", "UPDATE tbl_node_identity SET ed25519_public_key = @k", new { k = RandomNumberGenerator.GetBytes(32) });
    }

    [Fact]
    public async Task ARolledBackWriteLeavesTheVersionAlone_BecauseTheBumpIsInsideTheWritingTransaction()
    {
        var before = Version();
        using (var c = _factory.CreateConnection())
        {
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync("INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at) VALUES ('user', @w, @iv, 'now')",
                new { w = RandomNumberGenerator.GetBytes(49), iv = RandomNumberGenerator.GetBytes(12) }, tx);
            tx.Rollback();
        }
        Version().Should().Be(before);
    }

    [Fact]
    public async Task RunningTheMigrationsAgainChangesNeitherTheMarkerNorTheTriggers()
    {
        Execute("INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at) VALUES ('user', @w, @iv, 'now')",
            new { w = RandomNumberGenerator.GetBytes(49), iv = RandomNumberGenerator.GetBytes(12) });
        var before = Version();

        await new MigrationRunner(_factory).RunMigrationsAsync();

        Version().Should().Be(before, "INSERT OR IGNORE keeps the counter");
    }
}
