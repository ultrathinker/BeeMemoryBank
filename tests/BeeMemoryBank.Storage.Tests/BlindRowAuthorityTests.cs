using System.Reflection;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// A whitelist row for a blind node that already says superadmin / auto-accept — left by an older build,
/// copied from a peer, restored from a snapshot — must carry no authority (plan 3.2, BMB-42): migration
/// 033 clears it at rest, reads normalize a row written later, and the phone never arms rotation
/// auto-accept for it.
/// </summary>
public class BlindRowAuthorityTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private WhitelistRepository _repo = null!;
    private readonly Guid _blind = BlindNodeId.NewId();
    private readonly Guid _admin = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_blindrow_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new WhitelistRepository(_factory);
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration033_ClearsAuthorityOfBlindRows_InAnyCase_AndKeepsOrdinaryOnes(bool upperCase)
    {
        await InsertPollutedAsync(upperCase ? _blind.ToString().ToUpperInvariant() : _blind.ToString());
        await InsertPollutedAsync(_admin.ToString());

        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync(MigrationSql("033_blind_rows_never_superadmin.sql"));

        (await FlagsAsync(_blind)).Should().Be((0L, 0L, 0L), "a blind row keeps no authority at rest");
        (await FlagsAsync(_admin)).Should().Be((1L, 1L, 1L), "an ordinary superadmin is untouched");
    }

    [Fact]
    public async Task Reads_NormalizeAPollutedBlindRow_WrittenAfterTheMigration()
    {
        await InsertPollutedAsync(_blind.ToString().ToUpperInvariant());
        await InsertPollutedAsync(_admin.ToString());

        var row = (await _repo.GetByNodeIdAsync(_blind))!;
        row.IsSuperadmin.Should().BeFalse("the superadmin gate reads this row");
        row.AutoAcceptDekRotation.Should().BeFalse();
        row.AutoAcceptRestore.Should().BeFalse();
        (await _repo.GetAllActiveAsync()).Single(e => e.NodeId == _blind).IsSuperadmin.Should().BeFalse();
        (await _repo.GetAutoAcceptDekRotationAsync(_blind.ToString())).Should().BeFalse(
            "a blind node must never be trusted to rewrap the vault unattended");
        (await _repo.GetAutoAcceptRestoreAsync(_blind.ToString())).Should().BeFalse();

        (await _repo.GetByNodeIdAsync(_admin))!.IsSuperadmin.Should().BeTrue();
        (await _repo.GetAutoAcceptDekRotationAsync(_admin.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task PhoneAutoArm_SkipsAPollutedBlindRow()
    {
        await InsertPollutedAsync(_blind.ToString().ToUpperInvariant(), autoAccept: false);
        await InsertPollutedAsync(_admin.ToString(), autoAccept: false);

        using (var conn = _factory.CreateConnection())
            await PhoneRotationAutoArm.ArmAsync(conn);

        using var check = _factory.CreateConnection();
        (await check.ExecuteScalarAsync<long>(
            "SELECT auto_accept_dek_rotation FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = _blind.ToString() }))
            .Should().Be(0, "the phone must not arm unattended rotation from a blind node");
        (await check.ExecuteScalarAsync<long>(
            "SELECT auto_accept_dek_rotation FROM tbl_whitelist WHERE node_id = @id", new { id = _admin.ToString() }))
            .Should().Be(1, "a superadmin peer is still followed");
    }

    [Fact]
    public async Task Setters_NeverStoreAnAutoAcceptFlagForABlindNode()
    {
        await InsertPollutedAsync(_blind.ToString().ToUpperInvariant(), autoAccept: false);
        await InsertPollutedAsync(_admin.ToString(), autoAccept: false);

        await _repo.SetAutoAcceptRestoreAsync(_blind.ToString(), true);
        await _repo.SetAutoAcceptDekRotationAsync(_blind.ToString(), true);
        await _repo.SetAutoAcceptRestoreAsync(_admin.ToString(), true);
        await _repo.SetAutoAcceptDekRotationAsync(_admin.ToString(), true);

        (await FlagsAsync(_blind)).Should().Be((1L, 0L, 0L),
            "the flags must not exist at rest for a blind node, whoever asks (is_superadmin is migration 033's job)");
        (await FlagsAsync(_admin)).Should().Be((1L, 1L, 1L), "an ordinary peer is armed as asked");
    }

    private async Task<(long Super, long Rotation, long Restore)> FlagsAsync(Guid nodeId)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleAsync<(long, long, long)>(
            @"SELECT is_superadmin, auto_accept_dek_rotation, auto_accept_restore
              FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = nodeId.ToString() });
    }

    private async Task InsertPollutedAsync(string nodeId, bool autoAccept = true)
    {
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin,
                  auto_accept_dek_rotation, auto_accept_restore, created_at, updated_at)
              VALUES (@nodeId, 'x', @key, 'A', 1, @aa, @aa, @now, @now)",
            new { nodeId, key = new byte[32], aa = autoAccept ? 1 : 0, now });
    }

    private static string MigrationSql(string file)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}
