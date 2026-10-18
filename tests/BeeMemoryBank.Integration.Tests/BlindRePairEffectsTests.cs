using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-306/307 fix round 2 (T1, Q2): a blind node paired again from a fresh code is the same node at a renewed pairing. What the loop learned at the old pairing
/// (an unreachable pause, a closed window, a failure reason, the contact log's outbound facts) is forgotten whether or not the place moved and the new pairing is
/// tried at once; an audit line says so without the address; and the key the row is dialled by never changes through a pairing.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindRePairEffectsTests : IAsyncLifetime
{
    private const string Password = "blindRePairEffectsPw1!";
    private readonly BlindNodeFactory _blind = new(drainWaitSeconds: 10);
    private readonly BmbWebApplicationFactory _pc = new();
    private HttpClient _pcClient = null!;

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_blind.Server.CreateHandler());
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        // No background cycle: the test's own steps are the only writers of the loop's memory.
        _pc.Services.GetRequiredService<InvisibleModeService>().IsInvisible = true;
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> PairAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        var code = BlindPairCode.Parse(body.GetProperty("code").GetString()!);
        return await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
    }

    private async Task<(string? Address, string? Pin, string? Key)> RowAsync(Guid id)
    {
        using var conn = _pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.QuerySingleAsync<(string?, string?, string?)>(
            "SELECT api_address, tls_spki, hex(ed25519_public_key) FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = id.ToString() });
    }

    private async Task<List<(string Action, string Details)>> AuditAsync(Guid id)
    {
        using var conn = _pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<(string, string)>("SELECT action, details FROM tbl_audit_log WHERE entity_id = @id COLLATE NOCASE", new { id = id.ToString() })).ToList();
    }

    [Fact]
    public async Task A_blind_node_paired_again_forgets_what_the_loop_learned_at_the_old_pairing_and_says_so_without_the_address()
    {
        (await PairAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var address = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!.ApiAddress!;
        var contacts = _pc.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteOutboundFailure(blindId, DateTime.UtcNow, "no answer: the old pairing's failure", address);
        contacts.Get(blindId).OutboundFailure.Should().NotBeNull("precondition: the loop learned a failure at the old pairing");

        var again = await PairAsync();

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        contacts.Get(blindId).OutboundFailure.Should().BeNull("the same endpoint, but a renewed pairing: nothing learned at the old one counts");
        var line = (await AuditAsync(blindId)).Should().ContainSingle(l => l.Action == "peer_repaired").Which;
        line.Details.Should().NotContain(address).And.NotContain(new Uri(address).Host);
    }

    [Fact]
    public async Task A_pair_code_that_names_another_key_than_the_row_has_is_refused_and_changes_nothing()
    {
        (await PairAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using (var conn = _pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_whitelist SET ed25519_public_key = @key WHERE node_id = @id COLLATE NOCASE",
                new { key = Enumerable.Repeat((byte)9, 32).ToArray(), id = blindId.ToString() });
        var before = await RowAsync(blindId);

        var again = await PairAsync();

        again.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await again.Content.ReadAsStringAsync()).Should().Contain("another key");
        (await RowAsync(blindId)).Should().Be(before, "a pairing never changes the key a row is dialled by, nor anything else when it is refused");
        (await AuditAsync(blindId)).Should().NotContain(l => l.Action == "peer_repaired");
    }
}
