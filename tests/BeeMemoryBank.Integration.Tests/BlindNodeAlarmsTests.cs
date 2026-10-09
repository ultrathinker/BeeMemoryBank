using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-77, plan 5.6: when the PC calls a blind node "silent", and what it learns about the blind node's protocol. "Last
/// contact" is the last authenticated exchange in either direction, never the last event: a healthy blind node in a quiet vault
/// is not silent. Every judgement here runs on a fake clock handed to <see cref="BlindNodeManager"/>.
/// </summary>
public sealed class BlindNodeAlarmsTests : IAsyncLifetime
{
    private const string Password = "blindAlarmsPw1!";
    private const string Address = "https://blind.test:5610";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly BmbWebApplicationFactory _pc = new();
    private readonly Clock _clock = new();
    private BlindNodeManager _manager = null!;

    private DateTime Now => _clock.Now.UtcDateTime;

    public async Task InitializeAsync()
    {
        // The host's own sync loop dials the rows these tests add: it must reach nothing (the scripted blind node below is
        // handed to AfterSyncAsync directly).
        _pc.RouteOutboundHttpThrough(new NoNetwork());
        await _pc.InitializeNodeAsync("PC", Password);
        using var api = _pc.CreateClient();
        // The handshake in AfterSyncAsync signs with this node's key, which needs the vault open.
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

    private async Task<Guid> AddBlindRowAsync(DateTime createdAt, string? address = Address)
    {
        var id = BlindNodeId.NewId();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id,
            DisplayName = $"Blind {id.ToString()[..8]}",
            Ed25519PublicKey = new byte[32],
            ApiAddress = address,
            Status = "A",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        return id;
    }

    private Task PulledAtAsync(Guid id, DateTime at) =>
        _pc.Services.GetRequiredService<ISyncPositionRepository>().UpsertAsync(new SyncPosition
        {
            RemoteNodeId = id, LastSequenceNum = 0, UpdatedAt = at
        });

    private async Task<BlindNodeStatus> StatusOfAsync(Guid id) => (await _manager.ListAsync()).Single(s => s.NodeId == id);

    [Fact]
    public async Task AHealthyServerNode_InAQuietVault_IsNotSilent_FourDaysOn()
    {
        var id = await AddBlindRowAsync(Now);
        _clock.Now += TimeSpan.FromDays(4);
        // An empty pull still refreshes the position (SyncClient, F6): nothing was written in the vault for four days.
        await PulledAtAsync(id, Now - TimeSpan.FromMinutes(1));

        var status = await StatusOfAsync(id);

        status.Alarms.Should().BeEmpty("the PC reached it a minute ago; a quiet vault is not a silent node");
        status.LastContact.Should().Be(Now - TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task NoContactForLongerThanTheThreshold_IsSilent_AndNotBefore()
    {
        var id = await AddBlindRowAsync(Now - TimeSpan.FromDays(10));
        var lastPull = Now;
        await PulledAtAsync(id, lastPull);

        _clock.Now = lastPull + BlindNodeManager.SilentAfter - TimeSpan.FromMinutes(1);
        (await StatusOfAsync(id)).Alarms.Should().NotContain("silent");

        _clock.Now = lastPull + BlindNodeManager.SilentAfter + TimeSpan.FromMinutes(1);
        (await StatusOfAsync(id)).Alarms.Should().Contain("silent");
    }

    [Fact]
    public async Task ACopyThatCallsThisPc_IsNotSilent_ItsCallIsTheContact()
    {
        // A blind copy has no address: this PC never calls it, it calls this PC (which records the protocol it declares).
        var id = await AddBlindRowAsync(Now - TimeSpan.FromDays(10), address: null);
        await _pc.Services.GetRequiredService<IWhitelistRepository>()
            .RecordProtocolVersionAsync(id, SyncProtocolVersion.Current, Now - TimeSpan.FromHours(1));

        var status = await StatusOfAsync(id);

        status.Alarms.Should().BeEmpty();
        status.LastContact.Should().Be(Now - TimeSpan.FromHours(1));
        status.Protocol.Should().Be(SyncProtocolVersion.Current);
    }

    [Fact]
    public async Task ANodeAddedADayAgo_NeverHeardFrom_IsNotSilentYet()
    {
        var id = await AddBlindRowAsync(Now - TimeSpan.FromDays(1));

        var status = await StatusOfAsync(id);

        status.Alarms.Should().BeEmpty("the grace runs from when it was added");
        status.LastContact.Should().BeNull();
    }

    [Fact]
    public async Task AfterSync_TheBlindNodesAnswer_RecordsItsProtocolAndTheContact()
    {
        var id = await AddBlindRowAsync(Now - TimeSpan.FromDays(10));
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!;
        using var http = new HttpClient(new ScriptedBlindNode(id, SyncProtocolVersion.Current));

        await _manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);

        var status = await StatusOfAsync(id);
        status.Protocol.Should().Be(SyncProtocolVersion.Current, "the page said \"Protocol: unknown\" for every server blind node");
        status.LastContact.Should().Be(Now, "an answered my-standing call is contact, even when nothing was pulled");
        status.Alarms.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SyncProtocolVersion.Current + 1, "pc_too_old", "old_protocol")]
    [InlineData(SyncProtocolVersion.Current - 1, "old_protocol", "pc_too_old")]
    public async Task ABlindNodeOnAnotherProtocol_RaisesTheAlarmOfItsSide(int protocol, string raised, string notRaised)
    {
        var id = await AddBlindRowAsync(Now - TimeSpan.FromDays(10));
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!;
        using var http = new HttpClient(new ScriptedBlindNode(id, protocol));

        await _manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);

        var status = await StatusOfAsync(id);
        status.Protocol.Should().Be(protocol);
        status.Alarms.Should().Contain(raised).And.NotContain(notRaised).And.NotContain("silent");
    }

    [Fact]
    public async Task AnAnswerThatNamesAnotherNode_RecordsNothing()
    {
        var id = await AddBlindRowAsync(Now - TimeSpan.FromDays(1));
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!;
        using var http = new HttpClient(new ScriptedBlindNode(id, SyncProtocolVersion.Current + 1, responder: BlindNodeId.NewId()));

        await _manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);

        var status = await StatusOfAsync(id);
        status.Protocol.Should().BeNull();
        status.LastContact.Should().BeNull();
    }

    // ── the alarm list (BlindAlarmService) ──────────────────────────────────────────────────────

    private BlindNodeStatus Node(TimeSpan quietFor, string? address = Address, int? protocol = null, DateTime? protocolSeenAt = null) =>
        new(BlindNodeId.NewId(), "Blind under test", address, protocol, Now - quietFor, [],
            CreatedAt: Now - TimeSpan.FromDays(30), ProtocolSeenAt: protocolSeenAt);

    private static readonly IReadOnlyDictionary<Guid, DateTime> NoStreaks = new Dictionary<Guid, DateTime>();

    [Fact]
    public void AServerNodeThisPcCannotReach_IsWorthANotificationAfterSixHoursAwake_BeforeThePageBanner()
    {
        var node = Node(quietFor: TimeSpan.FromHours(7));

        var early = BlindAlarmService.Evaluate(Now, [node], new Dictionary<Guid, DateTime> { [node.NodeId] = Now - TimeSpan.FromHours(5.9) })
            .Should().ContainSingle("the failure goes on: a sleep of this PC restarts the streak, it does not end the outage").Subject;
        (early.Notify, early.Banner).Should().Be((false, false), "a night's sleep of the blind node's host is not an emergency, and nothing shows it");

        BlindAlarmService.Evaluate(Now, [node], NoStreaks)
            .Should().BeEmpty("a node nobody fails to reach, quiet for 7 hours, is only a quiet vault");

        var alarm = BlindAlarmService.Evaluate(Now, [node], new Dictionary<Guid, DateTime> { [node.NodeId] = Now - BlindAlarmService.ServerNotifyAfter })
            .Should().ContainSingle().Subject;
        alarm.Kind.Should().Be(BlindAlarmKinds.Silent);
        alarm.Notify.Should().BeTrue();
        alarm.Banner.Should().BeFalse("the page keeps its three days");
        alarm.Since.Should().Be(node.LastContact, "the episode is named by the last contact, which does not move while it lasts");
    }

    [Fact]
    public void AServerNodeSilentForThreeDays_IsABannerAndANotification_EvenWithoutAStreak()
    {
        var node = Node(quietFor: BlindNodeManager.SilentAfter + TimeSpan.FromMinutes(1));

        var alarm = BlindAlarmService.Evaluate(Now, [node], NoStreaks).Should().ContainSingle().Subject;

        (alarm.Kind, alarm.Notify, alarm.Banner).Should().Be((BlindAlarmKinds.Silent, true, true));
    }

    [Fact]
    public void ACopyThatCallsThisPc_IsWorthANotificationAfterItsThreshold()
    {
        var calls = Node(quietFor: BlindAlarmService.CopyNotifyAfter - TimeSpan.FromMinutes(1), address: null,
            protocol: SyncProtocolVersion.Current, protocolSeenAt: Now - TimeSpan.FromDays(3));
        BlindAlarmService.Evaluate(Now, [calls], NoStreaks).Should().BeEmpty();

        var late = calls with { LastContact = Now - BlindAlarmService.CopyNotifyAfter - TimeSpan.FromMinutes(1) };
        BlindAlarmService.Evaluate(Now, [late], NoStreaks).Should().ContainSingle().Which.Notify.Should().BeTrue();
    }

    [Fact]
    public void ACopyThatNeverCalledThisPc_KeepsItsPageBanner_ButIsNoNotification()
    {
        // It calls another node (a hub, a blind node): this PC has no evidence about it either way.
        var elsewhere = Node(quietFor: TimeSpan.FromDays(10), address: null) with { LastContact = null };

        var alarm = BlindAlarmService.Evaluate(Now, [elsewhere], NoStreaks).Should().ContainSingle().Subject;

        (alarm.Banner, alarm.Notify).Should().Be((true, false));
        alarm.Since.Should().Be(elsewhere.CreatedAt);
    }

    [Fact]
    public void TheProtocolAlarms_StayOneEpisode_WhileTheNodeAnswersEveryMinute()
    {
        var node = Node(quietFor: TimeSpan.FromMinutes(1), protocol: SyncProtocolVersion.Current + 1, protocolSeenAt: Now);

        var first = BlindAlarmService.Evaluate(Now, [node], NoStreaks);
        var minuteLater = BlindAlarmService.Evaluate(Now + TimeSpan.FromMinutes(1),
            [node with { LastContact = Now + TimeSpan.FromMinutes(1), ProtocolSeenAt = Now + TimeSpan.FromMinutes(1) }], NoStreaks);

        first.Should().ContainSingle().Which.Should().Be(new BlindAlarm(BlindAlarmKinds.PcTooOld, node.NodeId, node.DisplayName,
            Since: null, SyncProtocolVersion.Current + 1, Notify: true, Banner: true));
        minuteLater.Should().Equal(first);
    }

    [Fact]
    public void NothingIsJudged_UntilASyncCycleHasCompleted_OrWhenTheLastOneIsStale()
    {
        var service = new BlindAlarmService(_clock, invisible: null, session: null, unreachableSince: () => NoStreaks);
        var silent = Node(quietFor: TimeSpan.FromDays(4));

        service.Judge([silent]).Should().BeEquivalentTo(new BlindAlarmReport(BlindAlarmReport.WarmingUp, false, []),
            "this process has not tried to reach anyone yet");

        service.NoteSyncCycleCompleted();
        service.Judge([silent]).State.Should().Be(BlindAlarmReport.Judged);
        service.Judge([silent]).Alarms.Should().ContainSingle();

        // The computer slept: the contact times are as old as the last cycle before it.
        _clock.Now += BlindAlarmService.CycleFresh + TimeSpan.FromMinutes(1);
        service.Judge([silent]).State.Should().Be(BlindAlarmReport.WarmingUp);
    }

    [Fact]
    public void ACopyHeardFourDaysAgo_IsNotCalledSilent_RightAfterThisPcStartedOrWoke_ButIsOnceItHasHadItsChance()
    {
        var service = new BlindAlarmService(_clock, invisible: null, session: null, unreachableSince: () => NoStreaks);
        // A phone that called every hour until this PC was switched off 4 days ago.
        var copy = Node(quietFor: TimeSpan.FromDays(4), address: null, protocol: SyncProtocolVersion.Current, protocolSeenAt: Now - TimeSpan.FromDays(4));

        service.NoteSyncCycleCompleted(); // the first cycle after the start: this PC has not been awake long enough for a copy to call it
        var first = service.Judge([copy]);

        first.State.Should().Be(BlindAlarmReport.Judged);
        first.Alarms.Should().NotContain(a => a.Notify || a.Banner, "the phone had no chance to call: this PC was off");

        // Awake for the grace and still not a word from it: now it is silent.
        for (var minute = 1; minute <= 61; minute++)
        {
            _clock.Now += TimeSpan.FromMinutes(1);
            service.NoteSyncCycleCompleted();
        }
        var later = service.Judge([copy with { LastContact = Now - TimeSpan.FromDays(4) - TimeSpan.FromHours(1) }]);
        later.Alarms.Should().ContainSingle().Which.Should().Match<BlindAlarm>(a => a.Notify && a.Banner);
    }

    [Fact]
    public void AServerNode_IsNotHeldByTheCopyGrace()
    {
        var service = new BlindAlarmService(_clock, invisible: null, session: null, unreachableSince: () => NoStreaks);
        var server = Node(quietFor: TimeSpan.FromDays(4)); // this PC called it in the cycle that just ended: its contact time is fresh evidence

        service.NoteSyncCycleCompleted();

        service.Judge([server]).Alarms.Should().ContainSingle().Which.Should().Match<BlindAlarm>(a => a.Notify && a.Banner);
    }

    [Fact]
    public void TheCopyGraceBeginsAgain_WhenTheComputerSleepsAndWakes()
    {
        var service = new BlindAlarmService(_clock, invisible: null, session: null, unreachableSince: () => NoStreaks);
        var copy = Node(quietFor: TimeSpan.FromDays(4), address: null, protocol: SyncProtocolVersion.Current, protocolSeenAt: Now - TimeSpan.FromDays(4));
        service.NoteSyncCycleCompleted();
        for (var minute = 1; minute <= 61; minute++)
        {
            _clock.Now += TimeSpan.FromMinutes(1);
            service.NoteSyncCycleCompleted();
        }
        service.Judge([copy]).Alarms.Should().ContainSingle().Which.Notify.Should().BeTrue();

        _clock.Now += TimeSpan.FromHours(8); // the lid is closed
        service.NoteSyncCycleCompleted();    // the first cycle after waking

        service.Judge([copy]).Alarms.Should().NotContain(a => a.Notify || a.Banner, "it could not have called while the computer slept");
    }

    [Fact]
    public void InvisibleMode_JudgesNothing_AndACycleRunInItDoesNotCount()
    {
        var invisible = new BeeMemoryBank.Core.Services.InvisibleModeService();
        var service = new BlindAlarmService(_clock, invisible, session: null, unreachableSince: () => NoStreaks);
        var silent = Node(quietFor: TimeSpan.FromDays(4));
        service.NoteSyncCycleCompleted();

        invisible.IsInvisible = true;
        service.Judge([silent]).Should().BeEquivalentTo(new BlindAlarmReport(BlindAlarmReport.Invisible, false, []));

        service.NoteSyncCycleCompleted(); // a cycle while invisible contacts nobody
        invisible.IsInvisible = false;
        service.Judge([silent]).State.Should().Be(BlindAlarmReport.WarmingUp, "the contact times did not move while invisible");
    }

    [Fact]
    public void WhenNothingIsJudged_TheProtocolAlarmsAreStillThere_ForThePageButNotForANotification()
    {
        var invisible = new BeeMemoryBank.Core.Services.InvisibleModeService { IsInvisible = true };
        var service = new BlindAlarmService(_clock, invisible, session: null, unreachableSince: () => NoStreaks);
        var tooOld = Node(quietFor: TimeSpan.FromDays(4), protocol: SyncProtocolVersion.Current - 1, protocolSeenAt: Now);

        var invisibleReport = service.Judge([tooOld]);
        invisibleReport.State.Should().Be(BlindAlarmReport.Invisible);
        var alarm = invisibleReport.Alarms.Should().ContainSingle("the update banner was always shown in invisible mode, and a stale contact time does not touch it").Subject;
        (alarm.Kind, alarm.Notify, alarm.Banner).Should().Be((BlindAlarmKinds.OldProtocol, false, true));

        invisible.IsInvisible = false; // and before the first sync cycle completes
        var warming = service.Judge([tooOld]);
        warming.State.Should().Be(BlindAlarmReport.WarmingUp);
        warming.Alarms.Should().ContainSingle().Which.Kind.Should().Be(BlindAlarmKinds.OldProtocol);
    }

    [Fact]
    public async Task ALockedVault_GivesTheAlarmsWithoutNames()
    {
        var session = _pc.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
        var service = new BlindAlarmService(_clock, invisible: null, session, unreachableSince: null);
        var silent = Node(quietFor: TimeSpan.FromDays(4));
        service.Judge([silent]).Alarms.Should().ContainSingle().Which.Name.Should().Be(silent.DisplayName);

        using var api = _pc.CreateClient();
        (await api.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        var report = service.Judge([silent]);
        report.Locked.Should().BeTrue();
        report.Alarms.Should().ContainSingle("an alarm needs no key: it is still raised").Which.Name.Should().BeNull();
    }

    [Fact]
    public async Task TheAlarmsRoute_NeedsTheInternalKeyAndASuperadmin()
    {
        using var anonymous = _pc.Server.CreateClient();
        // Without the internal key the public-surface gate answers as if the route did not exist.
        (await anonymous.GetAsync("/api/blind-nodes/alarms")).StatusCode.Should()
            .BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        using var user = _pc.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", "user");
        (await user.GetAsync("/api/blind-nodes/alarms")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheAlarmsRoute_AnswersTheJudgedList_OnceTheSyncLoopHasRun()
    {
        var id = await AddBlindRowAsync(DateTime.UtcNow, address: null);
        await _pc.Services.GetRequiredService<IWhitelistRepository>()
            .RecordProtocolVersionAsync(id, SyncProtocolVersion.Current + 1, DateTime.UtcNow);
        using var api = _pc.CreateClient();

        var report = await JudgedAlarmsAsync(api);

        report.GetProperty("locked").GetBoolean().Should().BeFalse();
        var alarm = report.GetProperty("alarms").EnumerateArray().Should().ContainSingle().Subject;
        alarm.GetProperty("kind").GetString().Should().Be("pc_too_old");
        alarm.GetProperty("nodeId").GetGuid().Should().Be(id);
        alarm.GetProperty("protocol").GetInt32().Should().Be(SyncProtocolVersion.Current + 1);
        alarm.GetProperty("notify").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// GET /api/blind-nodes/alarms once it judges: the host's sync loop completes its first cycle a few seconds after start
    /// (it has no peers to call), and until then the answer is "warming_up".
    /// </summary>
    internal static async Task<JsonElement> JudgedAlarmsAsync(HttpClient api)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var report = await api.GetFromJsonAsync<JsonElement>("/api/blind-nodes/alarms");
            var state = report.GetProperty("state").GetString();
            if (state == BlindAlarmReport.Judged) return report;
            state.Should().Be(BlindAlarmReport.WarmingUp);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the sync loop did not complete a cycle within a minute");
            await Task.Delay(250);
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No network in this test.");
    }

    /// <summary>
    /// The three calls a PC makes on a blind node after a sync: challenge, authenticate (the token is not checked by this
    /// script; the real handshake is covered by the pairing tests) and my-standing, which answers with <paramref name="protocol"/>.
    /// </summary>
    private sealed class ScriptedBlindNode(Guid nodeId, int protocol, Guid? responder = null) : HttpMessageHandler
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
                    ["responder_node_id"] = responder ?? nodeId,
                    ["protocol"] = protocol,
                    ["caller_is_superadmin"] = true,
                    ["reseed_needed"] = false,
                    ["peers"] = Array.Empty<object>(),
                },
                _ => throw new InvalidOperationException($"unexpected call {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }
}
