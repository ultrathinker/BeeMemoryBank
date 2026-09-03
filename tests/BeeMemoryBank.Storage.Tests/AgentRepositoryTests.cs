using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Covers <see cref="AgentRepository.ClearWrappedDekForOwnerAsync"/> — the H6 fix's demotion-time
/// primitive (also exercised indirectly through UserService.UpdateUserAsync in
/// BeeMemoryBank.Core.Tests.UserServiceTests). Only a superadmin's agent may carry a wrapped
/// master DEK; this method is how a demoted user's agents lose theirs.
/// </summary>
public class AgentRepositoryTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private AgentRepository _repo = null!;
    private int _ownerId;
    private int _otherOwnerId;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_agentrepo_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _repo = new AgentRepository(_factory);

        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");
        _ownerId = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_user (username, display_name, password_hash, role, is_active, created_at)
              VALUES ('owner', 'Owner', 'hash', 'user', 1, @now); SELECT last_insert_rowid();",
            new { now });
        _otherOwnerId = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_user (username, display_name, password_hash, role, is_active, created_at)
              VALUES ('other', 'Other', 'hash', 'user', 1, @now); SELECT last_insert_rowid();",
            new { now });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> CreateAgentAsync(int ownerId, bool wrapped, string keySuffix)
    {
        var agent = new Agent
        {
            Name = "test-agent-" + keySuffix,
            KeyPrefix = "bee_" + keySuffix,
            KeyHash = "hash-" + keySuffix,
            EncryptedDek = wrapped ? new byte[] { 1, 2, 3 } : null,
            DekIV = wrapped ? new byte[] { 4, 5, 6 } : null,
            Salt = wrapped ? new byte[] { 7, 8, 9 } : null,
            KdfVersion = wrapped ? 1 : 0,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = ownerId
        };
        return await _repo.CreateAsync(agent);
    }

    [Fact]
    public async Task ClearWrappedDek_StripsKeyMaterial_ButLeavesAuthFieldsIntact()
    {
        var id = await CreateAgentAsync(_ownerId, wrapped: true, keySuffix: "a1");

        var affected = await _repo.ClearWrappedDekForOwnerAsync(_ownerId);

        affected.Should().Be(1);
        var reloaded = (await _repo.GetByIdAsync(id))!;
        reloaded.EncryptedDek.Should().BeNull();
        reloaded.DekIV.Should().BeNull();
        reloaded.Salt.Should().BeNull();
        reloaded.KdfVersion.Should().Be(0);
        reloaded.CanAutoUnlock.Should().BeFalse();

        // The key itself must keep authenticating -- only the vault-unlock capability is gone.
        reloaded.KeyHash.Should().Be("hash-a1");
        reloaded.KeyPrefix.Should().Be("bee_a1");
        reloaded.Status.Should().Be("A");
    }

    [Fact]
    public async Task ClearWrappedDek_DoesNotTouchAnotherOwnersAgents()
    {
        var mineId = await CreateAgentAsync(_ownerId, wrapped: true, keySuffix: "mine");
        var otherId = await CreateAgentAsync(_otherOwnerId, wrapped: true, keySuffix: "other");

        await _repo.ClearWrappedDekForOwnerAsync(_ownerId);

        (await _repo.GetByIdAsync(mineId))!.CanAutoUnlock.Should().BeFalse();
        (await _repo.GetByIdAsync(otherId))!.CanAutoUnlock.Should().BeTrue(
            "clearing one user's agents must never reach another user's agents");
    }

    [Fact]
    public async Task ClearWrappedDek_OnAnAlreadyUnwrappedAgent_IsANoOp_AndNotCounted()
    {
        await CreateAgentAsync(_ownerId, wrapped: false, keySuffix: "already-plain");

        var affected = await _repo.ClearWrappedDekForOwnerAsync(_ownerId);

        affected.Should().Be(0, "a row with no wrapped DEK to begin with is not a row this call changed");
    }

    [Fact]
    public async Task ClearWrappedDek_IsIdempotent()
    {
        await CreateAgentAsync(_ownerId, wrapped: true, keySuffix: "a1");

        (await _repo.ClearWrappedDekForOwnerAsync(_ownerId)).Should().Be(1);
        (await _repo.ClearWrappedDekForOwnerAsync(_ownerId)).Should().Be(0, "already cleared, second call changes nothing");
    }

    [Fact]
    public async Task ClearWrappedDek_ForOwnerWithNoAgents_ReturnsZero()
    {
        (await _repo.ClearWrappedDekForOwnerAsync(_ownerId)).Should().Be(0);
    }
}
