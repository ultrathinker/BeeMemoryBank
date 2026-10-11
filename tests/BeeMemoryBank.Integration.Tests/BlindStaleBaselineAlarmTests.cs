using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the Blind nodes page learns from a blind node's my-standing answer after Step A (docs/design/step-a-spec.md 5.1, 10.4): its store
/// summary and capabilities, the stale-baseline alarm (a baseline older than 7 days while deltas are held), and the protocol alarms judged
/// against the protocol this PC declares once it has activated.
/// </summary>
public sealed class BlindStaleBaselineAlarmTests : IAsyncLifetime
{
    private const string Password = "blindStalePw1!";
    private const string Address = "https://blind.test:5610";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly BmbWebApplicationFactory _pc = new();
    private readonly Clock _clock = new();
    private BlindNodeManager _manager = null!;
    private DateTime Now => _clock.Now.UtcDateTime;

    public async Task InitializeAsync()
    {
        _pc.RouteOutboundHttpThrough(new NoNetwork());
        await _pc.InitializeNodeAsync("PC", Password);
        using var api = _pc.CreateClient();
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        var sp = _pc.Services;
        _manager = new BlindNodeManager(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SpkiPinRegistry>(), NullLogger<BlindNodeManager>.Instance, _clock);
    }

    public Task DisposeAsync()
    {
        _pc.Dispose();
        return Task.CompletedTask;
    }

    private async Task<WhitelistEntry> AddBlindRowAsync()
    {
        var id = BlindNodeId.NewId();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Blind " + id.ToString()[..4], Ed25519PublicKey = new byte[32], ApiAddress = Address, Status = "A",
            CreatedAt = Now - TimeSpan.FromDays(30), UpdatedAt = Now - TimeSpan.FromDays(30),
        });
        return (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!;
    }

    private async Task<BlindNodeStatus> AfterSyncAsync(WhitelistEntry row, object? store, string[]? capabilities, int protocol = 4)
    {
        using var http = new HttpClient(new ScriptedBlindNode(row.NodeId, protocol, store, capabilities));
        await _manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);
        return (await _manager.ListAsync()).Single(n => n.NodeId == row.NodeId);
    }

    private object Store(TimeSpan baselineAge, long deltas) => new
    {
        baselineId = Guid.NewGuid(), baselineProducer = Guid.NewGuid(), baselineCreatedAt = Now - baselineAge, keyGeneration = 1,
        deltaCount = deltas, deltaBytes = deltas * 100, blobCount = 3, blobBytes = 300, storedBytes = 5000,
    };

    [Fact]
    public async Task The_store_and_capabilities_in_the_answer_are_kept_and_a_baseline_over_a_week_old_with_deltas_is_flagged()
    {
        var row = await AddBlindRowAsync();

        var status = await AfterSyncAsync(row, Store(TimeSpan.FromDays(8), deltas: 12), [MetadataCapabilities.EnvelopeV1]);

        status.Store.Should().NotBeNull();
        status.Store!.DeltaCount.Should().Be(12);
        status.Capabilities.Should().Equal(MetadataCapabilities.EnvelopeV1);
        status.Alarms.Should().Contain(BlindAlarmKinds.StaleBaseline);
    }

    [Theory]
    [InlineData(6, 12, false)]   // under a week
    [InlineData(8, 0, false)]    // nothing to replay: the baseline is the whole state
    [InlineData(8, 1, true)]
    public async Task The_stale_baseline_alarm_needs_both_age_and_deltas(int ageDays, long deltas, bool raised)
    {
        var row = await AddBlindRowAsync();

        var status = await AfterSyncAsync(row, Store(TimeSpan.FromDays(ageDays), deltas), [MetadataCapabilities.EnvelopeV1]);

        status.Alarms.Contains(BlindAlarmKinds.StaleBaseline).Should().Be(raised);
    }

    [Fact]
    public async Task A_node_that_says_nothing_about_its_store_gets_no_stale_baseline_alarm()
    {
        var row = await AddBlindRowAsync();

        var status = await AfterSyncAsync(row, store: null, capabilities: null);

        status.Store.Should().BeNull();
        status.Alarms.Should().NotContain(BlindAlarmKinds.StaleBaseline);
    }

    [Fact]
    public void The_stale_baseline_alarm_is_a_banner_and_no_notification_and_its_episode_is_the_baselines_age()
    {
        var created = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var node = new BlindNodeStatus(Guid.NewGuid(), "B", Address, 4, new DateTime(2026, 10, 9, 11, 0, 0, DateTimeKind.Utc), [],
            Store: new BlindStoreSummary(Guid.NewGuid(), null, created, 1, 5, 500, 1, 10, 1000));

        var alarms = BlindAlarmService.Evaluate(new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), [node], new Dictionary<Guid, DateTime>());

        var alarm = alarms.Single(a => a.Kind == BlindAlarmKinds.StaleBaseline);
        alarm.Banner.Should().BeTrue();
        alarm.Notify.Should().BeFalse("a week-old restore point is for the page, not for a notification");
        alarm.Since.Should().Be(created);
    }

    // ── protocol alarms against what this PC declares (spec 5.6) ───────────────────────────────────

    private ISyncProtocolPolicy ActivatedPolicy()
    {
        var policy = new SyncProtocolPolicy(_pc.Services.GetRequiredService<IDbConnectionFactory>());
        policy.MarkActivated();
        return policy;
    }

    [Theory]
    [InlineData(3, false, true)]    // 2.5 blind node after the activation: cut off, update it
    [InlineData(3, true, false)]    // a 2.6 node that has not applied the activation yet declares 3 with the capability: fine
    [InlineData(4, false, false)]
    [InlineData(2, false, true)]
    public void After_the_activation_an_old_blind_node_is_flagged_unless_it_is_a_2_6_node_still_on_3(int protocol, bool envelope, bool old)
    {
        var node = new BlindNodeStatus(Guid.NewGuid(), "B", Address, protocol, Now, [], Capabilities: envelope ? [MetadataCapabilities.EnvelopeV1] : null);

        var alarms = BlindAlarmService.Evaluate(Now, [node], new Dictionary<Guid, DateTime>(), policy: ActivatedPolicy());

        alarms.Any(a => a.Kind == BlindAlarmKinds.OldProtocol).Should().Be(old);
    }

    [Fact]
    public void A_blind_node_on_a_protocol_this_build_cannot_apply_is_flagged_for_the_pc_to_update()
    {
        var node = new BlindNodeStatus(Guid.NewGuid(), "B", Address, 5, Now, []);

        var alarms = BlindAlarmService.Evaluate(Now, [node], new Dictionary<Guid, DateTime>(), policy: ActivatedPolicy());

        alarms.Should().Contain(a => a.Kind == BlindAlarmKinds.PcTooOld);
    }

    [Fact]
    public void Without_a_policy_the_protocol_alarms_are_the_ones_of_2_5()
    {
        var older = new BlindNodeStatus(Guid.NewGuid(), "B", Address, SyncProtocolVersion.Current - 1, Now, []);
        var newer = new BlindNodeStatus(Guid.NewGuid(), "B", Address, SyncProtocolVersion.Current + 1, Now, []);

        var alarms = BlindAlarmService.Evaluate(Now, [older, newer], new Dictionary<Guid, DateTime>());

        alarms.Select(a => a.Kind).Should().BeEquivalentTo([BlindAlarmKinds.OldProtocol, BlindAlarmKinds.PcTooOld]);
    }

    [Fact]
    public void The_starter_hands_the_hosts_policy_to_the_alarm_service_it_makes_the_host_build()
    {
        var alarms = new BlindAlarmService(_clock);
        _ = new BlindAlarmStarter(alarms, ActivatedPolicy());
        var node = new BlindNodeStatus(Guid.NewGuid(), "B", Address, 3, Now - TimeSpan.FromMinutes(1), []);

        var report = alarms.Judge([node]);

        report.Alarms.Should().Contain(a => a.Kind == BlindAlarmKinds.OldProtocol, "this PC declares 4 now, and the node declares 3 with no capability");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No network in this test.");
    }

    /// <summary>The calls a PC makes on a blind node after a sync, with a my-standing answer that carries the Step A fields.</summary>
    private sealed class ScriptedBlindNode(Guid nodeId, int protocol, object? store, string[]? capabilities) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/api/sync/challenge" => new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = nodeId },
                "/api/sync/authenticate" => new { token = "scripted-token" },
                "/api/sync/my-standing" => new Dictionary<string, object?>
                {
                    ["responder_node_id"] = nodeId,
                    ["protocol"] = protocol,
                    ["caller_is_superadmin"] = true,
                    ["reseed_needed"] = false,
                    ["peers"] = Array.Empty<object>(),
                    ["capabilities"] = capabilities,
                    ["app_version"] = "2.6.0",
                    ["activated"] = true,
                    ["store"] = store,
                },
                _ => throw new InvalidOperationException($"unexpected call {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
            });
        }
    }
}
