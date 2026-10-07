using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Migration 036 and the repository for the trust mode of a callable node (ADR 0007): the column exists, an existing pin
/// becomes the <c>pin</c> mode, nothing else is touched, and a row round-trips with its mode.
/// </summary>
public class WhitelistTlsTrustTests : IAsyncLifetime
{
    private const string Pin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private DbConnectionFactory _factory = null!;
    private WhitelistRepository _repo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_tlstrust_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new WhitelistRepository(_factory);
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task TheColumnExists_AndANewRowStartsWithNoMode()
    {
        using var conn = _factory.CreateConnection();
        (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_whitelist')")).Should().Contain("tls_trust");

        var id = Guid.NewGuid();
        await _repo.CreateAsync(Row(id));
        var row = (await _repo.GetByNodeIdAsync(id))!;
        row.TlsTrust.Should().BeNull();
        row.EffectiveTlsTrust.Should().BeNull();
    }

    [Fact]
    public async Task Migration036_TurnsAnExistingPinIntoThePinMode_AndLeavesEveryOtherRowAlone()
    {
        var pinned = Guid.NewGuid();
        var noPin = Guid.NewGuid();
        var emptyPin = Guid.NewGuid();
        var publicCa = Guid.NewGuid();
        // Rows as an older build left them: a pin and no mode.
        await InsertAsync(pinned, Pin, null);
        await InsertAsync(noPin, null, null);
        await InsertAsync(emptyPin, "", null);
        await InsertAsync(publicCa, null, "public-ca");

        using (var conn = _factory.CreateConnection())
            foreach (var statement in BackfillStatements())
                await conn.ExecuteAsync(statement);

        (await TrustOfAsync(pinned)).Should().Be("pin");
        (await TrustOfAsync(noPin)).Should().BeNull();
        (await TrustOfAsync(emptyPin)).Should().BeNull();
        (await TrustOfAsync(publicCa)).Should().Be("public-ca");
    }

    [Fact]
    public async Task ARowKeepsItsMode_ThroughCreateReadAndUpdate_AndAPinWithoutOneReadsAsPin()
    {
        var hub = Guid.NewGuid();
        var entry = Row(hub);
        entry.TlsTrust = BlindTrust.PublicCa;
        await _repo.CreateAsync(entry);

        var read = (await _repo.GetByNodeIdAsync(hub))!;
        (read.TlsTrust, read.TlsSpki).Should().Be((BlindTrust.PublicCa, null));
        (await _repo.GetAllActiveAsync()).Single(e => e.NodeId == hub).TlsTrust.Should().Be(BlindTrust.PublicCa);

        read.TlsTrust = BlindTrust.Pin;
        read.TlsSpki = Pin;
        await _repo.UpdateAsync(read);
        read = (await _repo.GetByNodeIdAsync(hub))!;
        (read.TlsTrust, read.TlsSpki).Should().Be((BlindTrust.Pin, Pin));

        read.TlsTrust = null;
        await _repo.UpdateAsync(read);
        var legacy = (await _repo.GetByNodeIdAsync(hub))!;
        legacy.TlsTrust.Should().BeNull();
        legacy.EffectiveTlsTrust.Should().Be(BlindTrust.Pin, "a pin without a mode is the pin mode");
    }

    private static WhitelistEntry Row(Guid id) => new()
    {
        NodeId = id, DisplayName = "node", Ed25519PublicKey = new byte[32], Status = "A", ApiAddress = "https://bmb.example.org",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private async Task InsertAsync(Guid id, string? spki, string? trust)
    {
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, tls_spki, tls_trust, created_at, updated_at)
              VALUES (@id, 'x', @key, 'A', @spki, @trust, @now, @now)",
            new { id = id.ToString(), key = new byte[32], spki, trust, now });
    }

    private async Task<string?> TrustOfAsync(Guid id)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<string?>("SELECT tls_trust FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = id.ToString() });
    }

    /// <summary>The data statements of migration 036 (the column was added when the database was built).</summary>
    private static IEnumerable<string> BackfillStatements()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("036_whitelist_tls_trust.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var sql = string.Join('\n', reader.ReadToEnd().Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var statements = sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        statements.Should().Contain(s => s.StartsWith("ALTER TABLE tbl_whitelist ADD COLUMN tls_trust", StringComparison.Ordinal));
        return statements.Where(s => s.StartsWith("UPDATE", StringComparison.Ordinal));
    }
}
