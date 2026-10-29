using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-313 fix2 (Q3): the contact of a blind peer is ONE projection (<c>PeerContactView</c>), and three places show it: <c>GET /api/blind/status</c> (<c>peers[]</c>), the
/// Blind nodes page (<c>BlindNodeManager.ListAsync</c>) and the alarm (<c>not_answering</c>). For the same facts in the contact log they must say the same state, direction,
/// timestamps and reason: a field mapped differently in one of them fails here, not on somebody's screen.
/// </summary>
public sealed class BlindContactProjectionTests : IAsyncLifetime
{
    private const string Password = "blindProjectionPw1!";
    private const string Address = "https://blind.projection.test:5610";
    private readonly BmbWebApplicationFactory _pc = new();
    private IDisposable? _schedulerPause;

    public async Task InitializeAsync()
    {
        await _pc.InitializeNodeAsync("PC", Password);
        _schedulerPause = await _pc.Services.GetRequiredService<SyncScheduler>().PauseAsync();
    }

    public Task DisposeAsync()
    {
        _schedulerPause?.Dispose();
        _pc.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> BlindRowAsync()
    {
        var id = BlindNodeId.NewId();
        var created = DateTime.UtcNow.AddDays(-3);
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Projection blind", Ed25519PublicKey = new byte[32], ApiAddress = Address, Status = "A", CreatedAt = created, UpdatedAt = created,
        });
        return id;
    }

    private async Task<JsonElement> StatusPeerAsync(Guid id)
    {
        using var client = _pc.CreateClient();
        var json = await client.GetFromJsonAsync<JsonElement>("/api/blind/status");
        return json.GetProperty("peers").EnumerateArray().Single(p => p.GetProperty("node_id").GetString() == id.ToString());
    }

    private static DateTime? Time(JsonElement peer, string name) =>
        peer.GetProperty(name) is { ValueKind: JsonValueKind.String } v ? v.GetDateTime().ToUniversalTime() : null;

    [Fact]
    public async Task A_peer_this_pc_cannot_call_is_told_the_same_by_the_status_the_blind_page_and_the_alarm()
    {
        var id = await BlindRowAsync();
        var failingSince = DateTime.UtcNow.AddHours(-7);   // past BlindAlarmService.ServerNotifyAfter (6 h): the banner alarm exists
        var contacts = _pc.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteOutboundOk(id, failingSince.AddHours(-1), Address);
        // A cycle a minute for seven hours would be 420 calls; every ten minutes is one streak too (a gap of a whole StreakBreak would start another).
        for (var at = failingSince; at < DateTime.UtcNow.AddMinutes(-5); at = at.AddMinutes(10))
            contacts.NoteOutboundFailure(id, at, "it answered 403: its door is closed to this node", Address);
        var manager = _pc.Services.GetRequiredService<BlindNodeManager>();

        var status = await StatusPeerAsync(id);
        var nodes = await manager.ListAsync();
        var node = nodes.Single(n => n.NodeId == id);
        // The judgement itself, as the alarm service makes it, on the nodes the Blind page lists: the host's own sync loop may have put the row in its unreachable
        // streak by now (which has an alarm of its own), and that is not what is compared here.
        node.Contact.Should().NotBeNull();
        node.Contact!.State.Should().Be("not_answering", "the contact the Blind page lists: " + System.Text.Json.JsonSerializer.Serialize(node.Contact));
        var alarms = BlindAlarmService.Evaluate(DateTime.UtcNow, nodes, new Dictionary<Guid, DateTime>());
        var alarm = alarms.Where(a => a.NodeId == id && a.Kind == BlindAlarmKinds.NotAnswering).ToList().Should().ContainSingle("the alarms were: " + string.Join(", ", alarms.Select(a => a.Kind))).Which;

        status.GetProperty("state").GetString().Should().Be("not_answering");
        node.Contact!.State.Should().Be(status.GetProperty("state").GetString());
        status.GetProperty("contact_direction").GetString().Should().Be(node.Contact.LastContactDirection).And.Be("outbound");
        Time(status, "contact_at").Should().Be(node.Contact.LastContactAt?.ToUniversalTime()).And.NotBeNull();
        Time(status, "last_outbound_at").Should().Be(node.Contact.LastOutboundAt?.ToUniversalTime());
        Time(status, "last_inbound_at").Should().Be(node.Contact.LastInboundAt?.ToUniversalTime());
        Time(status, "not_answering_since").Should().Be(node.Contact.NotAnsweringSince?.ToUniversalTime()).And.Be(failingSince);
        status.GetProperty("not_answering_reason").GetString().Should().Be(node.Contact.NotAnsweringReason).And.NotBeNullOrWhiteSpace();   // (the host's own loop may have written its own reason since: the two must still agree)
        alarm.Since!.Value.ToUniversalTime().Should().Be(status.GetProperty("not_answering_since").GetDateTime().ToUniversalTime(), "the alarm says 'since' by the same projection");
        status.GetProperty("online").GetBoolean().Should().Be(node.Contact.Online).And.BeFalse();
    }

    [Fact]
    public async Task A_peer_that_has_answered_is_answering_in_the_status_and_the_blind_page_and_has_no_alarm()
    {
        var id = await BlindRowAsync();
        var answered = DateTime.UtcNow.AddSeconds(-20);
        _pc.Services.GetRequiredService<PeerContactLog>().NoteOutboundOk(id, answered, Address);
        var manager = _pc.Services.GetRequiredService<BlindNodeManager>();

        var status = await StatusPeerAsync(id);
        var node = (await manager.ListAsync()).Single(n => n.NodeId == id);

        status.GetProperty("state").GetString().Should().Be("answering").And.Be(node.Contact!.State);
        Time(status, "last_outbound_at").Should().Be(answered).And.Be(node.Contact.LastOutboundAt?.ToUniversalTime());
        status.GetProperty("not_answering_since").ValueKind.Should().Be(JsonValueKind.Null);
        (await manager.GetAlarmsAsync()).Alarms.Should().NotContain(a => a.NodeId == id && a.Kind == BlindAlarmKinds.NotAnswering);
    }
}
