using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// BMB-337: <see cref="WhitelistRepository.FillMissingTlsTrustAsync"/> gives a row the TLS mode and key a trusted node reported, one conditional statement, only for an
/// active row that has neither AND whose TLS trust no event ever wrote (the TLS concern is still undated, migration 045), and only while the node the key comes from
/// is an active superadmin row. It is local bookkeeping: no event, no version column, no <c>updated_at</c>. The one thing the schema does about it is the
/// <c>unlock_material_version</c> trigger of migration 040, a local counter that makes a sealed node rewrite its own boot-store mirror of the whitelist.
/// </summary>
public class WhitelistFillTlsTrustTests : IAsyncLifetime
{
    private const string Pin = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";
    private const string OtherPin = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI";
    private static readonly Guid Author = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    private DbConnectionFactory _factory = null!;
    private WhitelistRepository _repo = null!;
    private Guid _responder;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_filltls_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new WhitelistRepository(_factory);
        _responder = await AddAsync(superadmin: true);
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    /// <param name="datedTls">The TLS trust was written by an event (what a whitelist_update that turned it off leaves: empty columns, the concern dated). Otherwise the row is as
    /// migration 045 leaves a row with a version: every concern at it, the fields undated.</param>
    private async Task<Guid> AddAsync(string? trust = null, string? spki = null, string status = "A", bool superadmin = false, bool datedTls = false)
    {
        var row = new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "node", Ed25519PublicKey = new byte[32], Status = "A", ApiAddress = "https://blind.example:5610", IsSuperadmin = superadmin,
            TlsTrust = trust, TlsSpki = spki, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        row.SetAllVersions(new RowVersion(10, Author));
        row.MarkUndated(datedTls ? WhitelistConcerns.None : WhitelistEntry.Fields);
        await _repo.CreateAsync(row);
        if (status == "R") await _repo.RevokeAsync(row.NodeId, new RowVersion(20, Author));
        return row.NodeId;
    }

    private async Task<Dictionary<string, object?>> RawAsync(Guid id)
    {
        using var conn = _factory.CreateConnection();
        var row = (IDictionary<string, object?>)(await conn.QuerySingleAsync("SELECT * FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = id.ToString() }));
        return new Dictionary<string, object?>(row);
    }

    private async Task<string> UnlockMaterialVersionAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<string>("SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'") ?? "";
    }

    private Task<bool> FillAsync(Guid id, string pin = Pin, Guid? responder = null) =>
        _repo.FillMissingTlsTrustAsync(id, BlindTrust.Pin, pin, responder ?? _responder);

    [Fact]
    public async Task ARowWithNeitherModeNorKey_TakesBoth_AndOnlyThoseTwoColumnsChange()
    {
        var id = await AddAsync();
        var before = await RawAsync(id);

        (await FillAsync(id)).Should().BeTrue();

        var after = await RawAsync(id);
        after["tls_trust"].Should().Be("pin");
        after["tls_spki"].Should().Be(Pin);
        // The key is a byte array: compared by content, not by reference.
        static bool Same(object? a, object? b) => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);
        var changed = after.Keys.Where(k => !Same(before[k], after[k])).ToList();
        changed.Should().BeEquivalentTo(new[] { "tls_trust", "tls_spki" },
            "the version of the row, the version of each concern, what is undated (the TLS concern stays undated) and updated_at are all as they were");
    }

    [Theory]
    [InlineData("", "")]       // empty strings are as empty as NULLs: a row a snapshot or a manifest wrote
    [InlineData(null, "")]
    [InlineData("", null)]
    public async Task EmptyStrings_CountAsEmpty(string? trust, string? spki)
    {
        var id = await AddAsync(trust, spki);

        (await FillAsync(id)).Should().BeTrue();

        var row = (await _repo.GetByNodeIdAsync(id))!;
        (row.TlsTrust, row.TlsSpki).Should().Be(("pin", Pin));
    }

    [Theory]
    [InlineData("pin", OtherPin)]
    [InlineData(null, OtherPin)]            // a pin with no mode
    [InlineData("public-ca", null)]
    [InlineData("public-ca", OtherPin)]     // a pin left behind next to another mode
    [InlineData("future-mode", null)]       // a mode a later build wrote
    [InlineData("pin", null)]               // a mode and no key: not "neither"
    public async Task AModeOrAKeyThatIsThere_IsNeverOverwritten(string? trust, string? spki)
    {
        var id = await AddAsync(trust, spki);
        var before = await RawAsync(id);

        (await FillAsync(id)).Should().BeFalse();

        (await RawAsync(id)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ATlsTrustAnEventWrote_IsNotFilled_EvenWhenBothColumnsAreEmpty()
    {
        // What a whitelist_update with tls_trust "none" leaves: empty columns and the concern dated by the event. An off stays off.
        var off = await AddAsync(datedTls: true);
        var before = await RawAsync(off);

        (await FillAsync(off)).Should().BeFalse();

        (await RawAsync(off)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task OnlyTheTlsConcernDecides_AnotherDatedFieldDoesNotMakeTheRowIneligible()
    {
        // A rename or an address change an event wrote dates THAT field; the TLS trust is still nobody's.
        var id = await AddAsync();
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_whitelist SET undated_fields = undated_fields & ~@name & ~@address WHERE node_id = @id COLLATE NOCASE",
                new { id = id.ToString(), name = (int)WhitelistConcerns.Name, address = (int)WhitelistConcerns.Address });

        (await FillAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task ARevokedRow_AndAnUnknownId_AreNotWritten()
    {
        var revoked = await AddAsync(status: "R");
        var before = await RawAsync(revoked);

        (await FillAsync(revoked)).Should().BeFalse();
        (await FillAsync(Guid.NewGuid())).Should().BeFalse();

        (await RawAsync(revoked)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task TheResponder_MustBeAnActiveSuperadminRow_AtTheMomentOfTheWrite()
    {
        var peer = await AddAsync();
        var plain = await AddAsync(superadmin: false);
        var revoked = await AddAsync(superadmin: true, status: "R");
        var blind = BlindNodeId.NewId();
        using (var conn = _factory.CreateConnection())   // a blind row that says superadmin (a hand edit, an older build): the id decides, not the row
            await conn.ExecuteAsync(
                "INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at) VALUES (@id, 'b', @key, 'A', 1, @now, @now)",
                new { id = blind.ToString().ToUpperInvariant(), key = new byte[32], now = DateTime.UtcNow.ToString("O") });
        var before = await RawAsync(peer);

        (await FillAsync(peer, responder: plain)).Should().BeFalse("a peer that is not a superadmin here");
        (await FillAsync(peer, responder: revoked)).Should().BeFalse("a revoked row");
        (await FillAsync(peer, responder: Guid.NewGuid())).Should().BeFalse("no row at all");
        (await FillAsync(peer, responder: blind)).Should().BeFalse("a blind node is never the source of a key");
        (await RawAsync(peer)).Should().BeEquivalentTo(before);

        // The same peer, the same key: a superadmin gives it.
        (await FillAsync(peer)).Should().BeTrue();
    }

    [Fact]
    public async Task AResponderDemotedBetweenTwoFills_GivesNothingMore()
    {
        var first = await AddAsync();
        var second = await AddAsync();

        (await FillAsync(first)).Should().BeTrue();
        var demoted = (await _repo.GetByNodeIdAsync(_responder))!;
        demoted.IsSuperadmin = false;
        await _repo.UpdateAsync(demoted);

        (await FillAsync(second)).Should().BeFalse("the authority is checked by the statement that writes, not once before a list of writes");
        (await _repo.GetByNodeIdAsync(second))!.TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task TheIdIsMatchedInEitherCase_AsTheOtherWritesDo()
    {
        var id = await AddAsync();
        using (var conn = _factory.CreateConnection())
            (await conn.ExecuteScalarAsync<string>("SELECT node_id FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = id.ToString() }))
                .Should().NotBeNull();

        (await FillAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task TwoFillsAtOnce_ExactlyOneWrites_AndTheOtherFindsTheKeyThere()
    {
        var id = await AddAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => FillAsync(id, i % 2 == 0 ? Pin : OtherPin))));

        results.Count(r => r).Should().Be(1, "the empty test and the write are one statement: no second writer finds the row still empty");
        var row = (await _repo.GetByNodeIdAsync(id))!;
        row.TlsSpki.Should().BeOneOf(Pin, OtherPin);
    }

    [Fact]
    public async Task TheFill_WritesNoEvent_AndTheOnlySchemaSideEffectIsTheLocalUnlockMaterialCounter()
    {
        // The trigger of migration 040 on tbl_whitelist (tls_spki, tls_trust among its columns) bumps one local counter: a sealed node rewrites its boot-store
        // mirror of the whitelist (which holds the pins) when the counter differs from the one it mirrored. It is not replicated, versions nothing and seals nothing.
        var id = await AddAsync();
        var counterBefore = await UnlockMaterialVersionAsync();
        using var conn = _factory.CreateConnection();
        var eventsBefore = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event");

        (await FillAsync(id)).Should().BeTrue();

        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event")).Should().Be(eventsBefore);
        long.Parse(await UnlockMaterialVersionAsync()).Should().Be(long.Parse(counterBefore) + 1);
        // A call that writes nothing does not touch the counter either.
        (await FillAsync(id, OtherPin)).Should().BeFalse();
        long.Parse(await UnlockMaterialVersionAsync()).Should().Be(long.Parse(counterBefore) + 1);
    }
}
