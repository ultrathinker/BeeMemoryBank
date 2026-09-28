using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The setup endpoints against a database that already belongs to somebody.
///
/// <para>R2's live mixed-version test (release A2, finding U2c) ran /api/init/standalone against a
/// vault in the pre-unification shape and it SUCCEEDED: a second tbl_node_identity row and a
/// setupadmin with a fresh master DEK, next to data still sealed under the old one. Recognizing the
/// legacy shape in <c>IsInitializedAsync</c> closes that particular door; this is the guard behind it,
/// which does not depend on anybody recognizing a shape at all — the database holds an identity row
/// or a key slot, so it is not empty, so nothing may be initialized into it.</para>
/// </summary>
public class LegacyVaultSetupRefusalTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "legacySetupPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static object StandaloneBody() => new
    {
        adminUsername = "setupadmin", displayName = "Fresh setup", password = "Another-password-1"
    };

    /// <summary>Rewrites this node's vault into the shape a pre-unification one has.</summary>
    private async Task MakeLegacyAsync()
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_key_slot SET slot_type = 'password'");
        await conn.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL");
        await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = 'legacy_password_unified'");
    }

    private async Task<(long Identities, long Users)> CountsAsync()
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_node_identity"),
                await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_user"));
    }

    [Fact]
    public async Task StandaloneSetup_OnALegacyVault_IsRefused_AndWritesNothing()
    {
        await MakeLegacyAsync();
        var before = await CountsAsync();
        using var client = _factory.CreateClient();

        // The node knows it is initialized (the legacy shape is recognized)…
        (await client.GetFromJsonAsync<JsonElement>("/api/init/status"))
            .GetProperty("initialized").GetBoolean().Should().BeTrue();

        // …and the setup path refuses on its own, without depending on that answer.
        var resp = await client.PostAsJsonAsync("/api/init/standalone", StandaloneBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("error").GetString().Should().Contain("already holds a node");

        (await CountsAsync()).Should().Be(before,
            "nothing was written: no second identity, no setupadmin, no new master key");
    }

    /// <summary>
    /// The shape that reads as uninitialized while an identity row exists: a restore bootstrap in
    /// progress. The node says "not initialized" on purpose (the restore must be allowed to finish),
    /// and the setup path must STILL refuse — the half-written identity is this node's, and a new
    /// master DEK written next to the recovery material's would be the only one the node knows.
    /// </summary>
    [Fact]
    public async Task StandaloneSetup_OnANodeMidRestore_IsRefused()
    {
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_key_slot");
            await conn.ExecuteAsync("DELETE FROM tbl_user");
            await conn.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = NULL");
        }
        await _factory.Services.GetRequiredService<RestoreBootstrapMarker>().SetAsync();
        var before = await CountsAsync();
        using var client = _factory.CreateClient();

        (await client.GetFromJsonAsync<JsonElement>("/api/init/status"))
            .GetProperty("initialized").GetBoolean().Should().BeFalse("the bootstrap is only half done");

        (await client.PostAsJsonAsync("/api/init/standalone", StandaloneBody()))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CountsAsync()).Should().Be(before, "and nothing was written over it");
    }

    /// <summary>The other direction, so the guard is not simply "setup never works": an empty node
    /// still initializes, and the wizard still works.</summary>
    [Fact]
    public async Task StandaloneSetup_OnAnEmptyDatabase_StillWorks()
    {
        using var empty = new BmbWebApplicationFactory();
        _ = empty.Services; // the host, with migrations run and nothing else
        using var client = empty.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/init/standalone", StandaloneBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        using var scope = empty.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync())
            .Should().BeTrue();
    }

    /// <summary>
    /// The rehearsal's vault (R2's REHEARSAL.md, on a copy of the owner's real server): an identity
    /// row whose <c>sentinel_value</c> is NULL, because 1.0.11 neither writes nor requires one. It is
    /// an initialized node — the whole point of the 1.0.11 semantics — and setup still refuses it,
    /// which is the pair the rehearsal needs to see.
    /// </summary>
    [Fact]
    public async Task AVaultWithANullSentinel_IsInitialized_AndSetupIsRefused()
    {
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = NULL");
        var before = await CountsAsync();
        using var client = _factory.CreateClient();

        (await client.GetFromJsonAsync<JsonElement>("/api/init/status"))
            .GetProperty("initialized").GetBoolean()
            .Should().BeTrue("1.0.11 says so, and the vault is intact behind that row");

        (await client.PostAsJsonAsync("/api/init/standalone", StandaloneBody()))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CountsAsync()).Should().Be(before);
    }
}