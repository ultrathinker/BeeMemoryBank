using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

public class MigrationTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private MigrationRunner _runner = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_test_{Guid.NewGuid():N}");
        _runner = new MigrationRunner(_factory);
        await _runner.RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunMigrations_AppliesWithoutErrors()
    {
        using var conn = _factory.CreateConnection();
        var version = await conn.QuerySingleAsync<int>("SELECT MAX(version) FROM tbl_migration");
        version.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task RunMigrations_IsIdempotent()
    {
        // repeated run should not throw exceptions
        await _runner.RunMigrationsAsync();

        using var conn = _factory.CreateConnection();
        var count = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_migration");
        count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Schema_AllTablesExist()
    {
        using var conn = _factory.CreateConnection();
        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        tables.Should().Contain("tbl_article");
        tables.Should().Contain("tbl_article_body");
        tables.Should().Contain("tbl_article_concept_tag");
        tables.Should().Contain("tbl_audit_log");
        tables.Should().Contain("tbl_conflict_version");
        tables.Should().Contain("tbl_concept_tag");
        tables.Should().Contain("tbl_folder_acl_entry");
        tables.Should().Contain("tbl_key_slot");
        tables.Should().Contain("tbl_migration");
        tables.Should().Contain("tbl_node_identity");
        tables.Should().Contain("tbl_whitelist");
        tables.Should().Contain("tbl_projection_matrix");

        tables.Should().NotContain("tbl_tag");
        tables.Should().NotContain("tbl_article_tag");
        tables.Should().NotContain("tbl_folder_restriction");
    }

    // Regression test for finding C1 / migration 012: any recovery slot created before the fix
    // has its Argon2id salt column set to the recovery key's own raw bytes — an unrecoverable
    // exposure that can only be closed by invalidating the slot outright (see the migration
    // file's own comment for the full rationale). This simulates an existing vault that already
    // had a (now-unsafe) recovery slot when it upgrades past this migration.
    [Fact]
    public async Task Migration012_RemovesExistingRecoverySlots_ButLeavesOtherSlotTypesAlone()
    {
        using (var conn = _factory.CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_key_slot
                  (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                  VALUES ('recovery', @dek, @iv, @salt, 65536, 3, 4, @now)",
                new { dek = new byte[] { 1, 2, 3 }, iv = new byte[] { 4, 5, 6 }, salt = new byte[] { 7, 8, 9 }, now });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_key_slot
                  (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                  VALUES ('user', @dek, @iv, @salt, 65536, 3, 4, @now)",
                new { dek = new byte[] { 10, 11, 12 }, iv = new byte[] { 13, 14, 15 }, salt = new byte[] { 16, 17, 18 }, now });

            // Force migration 012 to be re-applied, simulating a vault that had already run every
            // migration UP TO this one — its recovery slot (inserted above) predates the fix and
            // must be swept up the same way an upgrading production node's would be.
            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 12");
        }

        await _runner.RunMigrationsAsync();

        using var check = _factory.CreateConnection();
        var slotTypes = (await check.QueryAsync<string>("SELECT slot_type FROM tbl_key_slot")).ToList();
        slotTypes.Should().NotContain("recovery", "an unsafe pre-fix recovery slot must be removed, not silently kept around");
        slotTypes.Should().Contain("user", "only recovery slots are unsafe — password/user slots must survive untouched");
    }

    // Regression test for finding H6 / migration 014: before this fix, EVERY agent row wrapped
    // the master DEK regardless of owner, making an ordinary user's self-service agent key
    // cryptographically a key to the whole vault. This simulates a pre-fix database that already
    // has agents belonging to both a superadmin and a non-superadmin owner, then re-applies the
    // migration exactly as an upgrading production node would.
    [Fact]
    public async Task Migration014_ClearsWrappedDekForNonSuperadminAgents_ButLeavesSuperadminAgentsAlone()
    {
        int superadminId, regularUserId, deactivatedUserId;
        int superadminAgentId, regularAgentId, deactivatedOwnerAgentId;

        using (var conn = _factory.CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("o");

            superadminId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_user (username, display_name, password_hash, role, is_active, created_at)
                  VALUES ('admin', 'Admin', 'hash', 'superadmin', 1, @now); SELECT last_insert_rowid();",
                new { now });
            regularUserId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_user (username, display_name, password_hash, role, is_active, created_at)
                  VALUES ('bob', 'Bob', 'hash', 'user', 1, @now); SELECT last_insert_rowid();",
                new { now });
            // A deactivated (soft-deleted) non-superadmin owner. is_active must not matter to
            // the migration's decision — only role does — so this agent must be stripped just
            // like an active regular user's, not treated as some special third case.
            deactivatedUserId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_user (username, display_name, password_hash, role, is_active, created_at)
                  VALUES ('exuser_del_abc', 'Ex User', 'hash', 'user', 0, @now); SELECT last_insert_rowid();",
                new { now });

            // Pre-fix shape: every agent has a fully wrapped DEK, regardless of owner role.
            superadminAgentId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_agent
                    (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
                  VALUES ('admin-agent', 'bee_admin1234', 'hash-admin', @dek, @iv, 1, @salt, 'A', @now, @ownerId);
                  SELECT last_insert_rowid();",
                new { dek = new byte[] { 1, 2, 3 }, iv = new byte[] { 4, 5, 6 }, salt = new byte[] { 7, 8, 9 }, now, ownerId = superadminId });
            regularAgentId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_agent
                    (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
                  VALUES ('bob-agent', 'bee_bob123456', 'hash-bob', @dek, @iv, 1, @salt, 'A', @now, @ownerId);
                  SELECT last_insert_rowid();",
                new { dek = new byte[] { 10, 11, 12 }, iv = new byte[] { 13, 14, 15 }, salt = new byte[] { 16, 17, 18 }, now, ownerId = regularUserId });
            deactivatedOwnerAgentId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_agent
                    (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
                  VALUES ('exuser-agent', 'bee_exuser123', 'hash-exuser', @dek, @iv, 1, @salt, 'A', @now, @ownerId);
                  SELECT last_insert_rowid();",
                new { dek = new byte[] { 19, 20, 21 }, iv = new byte[] { 22, 23, 24 }, salt = new byte[] { 25, 26, 27 }, now, ownerId = deactivatedUserId });

            // Force migration 014 to be re-applied on top of this pre-fix data, simulating an
            // upgrading production node.
            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 14");
        }

        await _runner.RunMigrationsAsync();

        using var check = _factory.CreateConnection();
        var repo = new AgentRepository(_factory);

        var superadminAgent = (await repo.GetByIdAsync(superadminAgentId))!;
        superadminAgent.CanAutoUnlock.Should().BeTrue("a superadmin's pre-existing agent must keep its wrapped DEK");
        superadminAgent.EncryptedDek.Should().Equal(new byte[] { 1, 2, 3 });

        var regularAgent = (await repo.GetByIdAsync(regularAgentId))!;
        regularAgent.CanAutoUnlock.Should().BeFalse("an ordinary user's pre-existing agent must lose its wrapped DEK");
        regularAgent.EncryptedDek.Should().BeNull();
        regularAgent.DekIV.Should().BeNull();
        regularAgent.Salt.Should().BeNull();
        regularAgent.KdfVersion.Should().Be(0);
        // The key must stay valid for authentication -- only the vault-unlock capability is gone.
        regularAgent.KeyHash.Should().Be("hash-bob");
        regularAgent.Status.Should().Be("A");

        var deactivatedOwnerAgent = (await repo.GetByIdAsync(deactivatedOwnerAgentId))!;
        deactivatedOwnerAgent.CanAutoUnlock.Should().BeFalse(
            "is_active must not matter to this decision -- only role does");
    }

    // Migration 014 clears by the OWNER'S ROLE, so a SUPERADMIN's already-revoked agents kept
    // their wrapped DEK: revoked, but still a key to the whole vault for anyone holding the old
    // plaintext bee_... string and a copy of the database. Observed on a live node right after
    // 014 shipped. 015 closes it for existing rows; AgentRepository.DeleteAsync keeps it closed
    // for every revocation from here on.
    [Fact]
    public async Task Migration015_ClearsWrappedDekFromRevokedAgents_EvenSuperadminOwned()
    {
        int superadminAgentId, revokedSuperadminAgentId;

        using (var conn = _factory.CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("o");

            var superadminId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_user (username, display_name, password_hash, role, is_active, created_at)
                  VALUES ('root015', 'Root', 'hash', 'superadmin', 1, @now);
                  SELECT last_insert_rowid();", new { now });

            superadminAgentId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_agent
                    (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
                  VALUES ('live', 'bee_live015', 'hash-live015', @dek, @iv, 1, @salt, 'A', @now, @ownerId);
                  SELECT last_insert_rowid();",
                new { dek = new byte[] { 1, 2, 3 }, iv = new byte[] { 4, 5, 6 }, salt = new byte[] { 7, 8, 9 }, now, ownerId = superadminId });

            revokedSuperadminAgentId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_agent
                    (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
                  VALUES ('revoked', 'bee_rev015', 'hash-rev015', @dek, @iv, 1, @salt, 'D', @now, @ownerId);
                  SELECT last_insert_rowid();",
                new { dek = new byte[] { 10, 11, 12 }, iv = new byte[] { 13, 14, 15 }, salt = new byte[] { 16, 17, 18 }, now, ownerId = superadminId });

            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 15");
        }

        await _runner.RunMigrationsAsync();

        using var check = _factory.CreateConnection();
        var repo = new AgentRepository(_factory);

        // Read the revoked row straight from SQL: GetByIdAsync only returns active agents, which
        // is exactly why this row was easy to overlook in the first place.
        (await check.QuerySingleAsync<byte[]?>(
            "SELECT encrypted_dek FROM tbl_agent WHERE id = @revokedSuperadminAgentId",
            new { revokedSuperadminAgentId }))
            .Should().BeNull("a revoked agent must not keep key material, whoever owned it");
        (await check.QuerySingleAsync<byte[]?>(
            "SELECT dek_iv FROM tbl_agent WHERE id = @revokedSuperadminAgentId", new { revokedSuperadminAgentId }))
            .Should().BeNull();
        (await check.QuerySingleAsync<byte[]?>(
            "SELECT salt FROM tbl_agent WHERE id = @revokedSuperadminAgentId", new { revokedSuperadminAgentId }))
            .Should().BeNull();
        (await check.QuerySingleAsync<long>(
            "SELECT kdf_version FROM tbl_agent WHERE id = @revokedSuperadminAgentId", new { revokedSuperadminAgentId }))
            .Should().Be(0);
        (await check.QuerySingleAsync<string>(
            "SELECT key_hash FROM tbl_agent WHERE id = @revokedSuperadminAgentId", new { revokedSuperadminAgentId }))
            .Should().Be("hash-rev015", "the audit trail must survive — only the key material goes");

        (await repo.GetByIdAsync(superadminAgentId))!.CanAutoUnlock.Should().BeTrue(
            "an ACTIVE superadmin agent is untouched by this migration");
    }

    // ───────────── 017: blob store contract ─────────────
    //
    // Recreates the state a node is in right before 017 runs — inline `ciphertext` columns still
    // present alongside tbl_blob — by re-adding the columns to a fully migrated schema and deleting
    // 017's tbl_migration row, then re-runs the runner exactly as an upgrading node would.

    [Fact]
    public async Task Migration017_DropsInlineColumns_WhenEveryRowHasItsBlob()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await ArrangePre017StateAsync(bodyBytes: bytes, blobPresent: true);

        await _runner.RunMigrationsAsync();

        using var conn = _factory.CreateConnection();
        (await ColumnsOfAsync(conn, "tbl_article_body")).Should().NotContain("ciphertext");
        (await ColumnsOfAsync(conn, "tbl_article_version")).Should().NotContain("ciphertext");
        // The bytes are still reachable through the hash, and the index DROP COLUMN must keep
        // survived (agy's review flagged the create/copy/rename variant for losing it).
        var stored = await conn.QuerySingleAsync<byte[]>(
            "SELECT bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash");
        stored.Should().Equal(bytes);
        (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'tbl_article_version'"))
            .Should().Contain("idx_article_version_article");
    }

    [Fact]
    public async Task Migration017_RefusesToDrop_WhenARowWouldLoseItsOnlyCopy()
    {
        await ArrangePre017StateAsync(bodyBytes: [9, 9, 9], blobPresent: false);

        var act = () => _runner.RunMigrationsAsync();
        // The guard is a NOT NULL constraint violation — a constraint error, which the runner never
        // treats as idempotent — so the migration fails and the node does not start.
        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();

        using var conn = _factory.CreateConnection();
        (await ColumnsOfAsync(conn, "tbl_article_body")).Should().Contain("ciphertext",
            "the transaction rolled back; the inline copy is still the only copy and must survive");
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration WHERE version = 17")).Should().Be(0);
    }

    [Fact]
    public async Task Migration017_VacuumsAfterCommit_ReclaimingFreedPages()
    {
        await ArrangePre017StateAsync(bodyBytes: [1], blobPresent: true);
        using (var conn = _factory.CreateConnection())
        {
            // Leave a large hole in the file: freelist pages that only VACUUM gives back.
            await conn.ExecuteAsync("CREATE TABLE tbl_junk (x BLOB)");
            await conn.ExecuteAsync("INSERT INTO tbl_junk VALUES (@x)", new { x = new byte[512 * 1024] });
            await conn.ExecuteAsync("DROP TABLE tbl_junk");
            (await conn.ExecuteScalarAsync<long>("PRAGMA freelist_count")).Should().BeGreaterThan(0);
        }

        await _runner.RunMigrationsAsync();

        using var check = _factory.CreateConnection();
        (await check.ExecuteScalarAsync<long>("PRAGMA freelist_count")).Should().Be(0,
            "the runner must VACUUM after a migration carrying the bmb:vacuum-after marker");
    }

    private async Task ArrangePre017StateAsync(byte[] bodyBytes, bool blobPresent)
    {
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");
        await conn.ExecuteAsync("ALTER TABLE tbl_article_body ADD COLUMN ciphertext BLOB");
        await conn.ExecuteAsync("ALTER TABLE tbl_article_version ADD COLUMN ciphertext BLOB");

        var articleId = Guid.NewGuid().ToString();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'X', '/', 'A', @now, @now)",
            new { id = articleId, now });
        var hash = await conn.ExecuteScalarAsync<string>("SELECT sha256(@b)", new { b = bodyBytes });
        if (blobPresent)
            await conn.ExecuteAsync(
                "INSERT INTO tbl_blob (hash, data, size, created_at) VALUES (@hash, @b, length(@b), @now)",
                new { hash, b = bodyBytes, now });
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article_body (article_id, ciphertext, ciphertext_hash, iv, encrypted_dek, dek_iv)
              VALUES (@articleId, @b, @hash, X'00', X'00', X'00')",
            new { articleId, b = bodyBytes, hash });

        await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 17");
    }

    private static async Task<List<string>> ColumnsOfAsync(System.Data.IDbConnection conn, string table) =>
        (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')")).ToList();
}
