extern alias WebApp;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The hub shows what a blind copy's owner did on the device (release 2.7): the copy's report says its limits stop it ("Paused by the device's owner: ..."
/// instead of "silent"), or the owner removed it from the device (<c>POST /api/sync/leave</c>, "removed by the device's owner", and a Revoke button - the hub cannot
/// revoke by itself). Old-shape reports, without the fields, keep working.
/// </summary>
public class BlindPauseTests : IAsyncLifetime
{
    private const string Password = "blindPausePassword1";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = _factory.CreateClient();
        await _factory.InitializeNodeAsync("Hub", Password);
        (await _admin.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<(Guid Id, HttpClient Http)> PeerAsync(bool blind = true)
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var id = blind ? BlindNodeId.NewId() : Guid.NewGuid();
        await _factory.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = blind ? "Anna's phone" : "A full peer", Ed25519PublicKey = publicKey, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = id,
        });
        var token = _factory.Services.GetRequiredService<SyncTokenStore>().IssueToken(id, SyncProtocolVersion.Current);
        var http = _factory.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (id, http);
    }

    private async Task<long> HeadAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
    }

    private async Task<HttpResponseMessage> ReportAsync(HttpClient http, string? query = null) =>
        await http.PostAsync($"/api/sync/report-position?sequence={await HeadAsync()}{(query is null ? "" : "&" + query)}", null);

    private async Task<JsonElement> NodeOnPageAsync(Guid id)
    {
        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/", Web);
        return list.EnumerateArray().Single(n => n.GetProperty("nodeId").GetGuid() == id);
    }

    private static string? Text(JsonElement node, string name) =>
        node.GetProperty("contact").TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<JsonElement> NodeOnMapAsync(Guid id)
    {
        var map = await _admin.GetFromJsonAsync<JsonElement>("/api/sync/network", Web);
        return map.GetProperty("peers").EnumerateArray().Single(n => n.GetProperty("nodeId").GetGuid() == id);
    }

    /// <summary>The next midnight of a device at +03:00, as the device would send it: <c>2026-10-22T00:00:00+03:00</c>.</summary>
    private static string NextDeviceMidnight()
    {
        var offset = TimeSpan.FromHours(3);
        var local = DateTime.UtcNow + offset;
        return new DateTimeOffset(DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified), offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    // ── report-position ───────────────────────────────────────────────────

    [Fact]
    public async Task APausedReport_IsAnswered200_AndTheBlindNodesListSaysWhyInTheHubsWords()
    {
        var (id, http) = await PeerAsync();

        var response = await ReportAsync(http, $"paused=traffic&pausedUntil={Uri.EscapeDataString(NextDeviceMidnight())}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = await NodeOnPageAsync(id);
        Text(node, "pausedText").Should().Be("Paused by the device's owner: daily traffic limit (until 00:00 device time)");
        Text(node, "pausedReason").Should().Be("traffic");
        node.GetProperty("contact").GetProperty("pausedUntil").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task AReportWithoutTheFields_StillWorks_AndEndsAPause()
    {
        var (id, http) = await PeerAsync();
        (await ReportAsync(http, "paused=requests")).StatusCode.Should().Be(HttpStatusCode.OK);
        Text(await NodeOnPageAsync(id), "pausedText").Should().Be("Paused by the device's owner: daily request limit");

        (await ReportAsync(http)).StatusCode.Should().Be(HttpStatusCode.OK, "an old-shape report, as every copy before 2.7 sends it");

        Text(await NodeOnPageAsync(id), "pausedText").Should().BeNull("a report that names no reason is a copy that runs again");
    }

    [Fact]
    public async Task AnOldShapeReport_FromACopyThatNeverPaused_ChangesNothingAboutIt()
    {
        var (id, http) = await PeerAsync();

        (await ReportAsync(http)).StatusCode.Should().Be(HttpStatusCode.OK);

        var node = await NodeOnPageAsync(id);
        Text(node, "pausedText").Should().BeNull();
        node.GetProperty("contact").GetProperty("leftAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("paused=%3Cscript%3Ealert(1)%3C%2Fscript%3E", "Paused by the device's owner: a limit set on the device")]
    [InlineData("paused=traffic&pausedUntil=not-a-time", "Paused by the device's owner: daily traffic limit")]
    [InlineData("paused=disk&pausedUntil=1999-01-01T00:00:00Z", "Paused by the device's owner: storage limit")]
    [InlineData("paused=device-traffic&pausedUntil=9999-12-31T23:59:59Z", "Paused by the device's owner: daily traffic limit of the whole device")]
    public async Task WhatACopySays_NeverReachesAScreenAsItCame(string query, string expected)
    {
        var (id, http) = await PeerAsync();

        (await ReportAsync(http, query)).StatusCode.Should().Be(HttpStatusCode.OK);

        Text(await NodeOnPageAsync(id), "pausedText").Should().Be(expected);
        (await NodeOnMapAsync(id)).GetProperty("pausedText").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task OnlyABlindCopy_CanBeShownAsPaused()
    {
        var (id, http) = await PeerAsync(blind: false);

        (await ReportAsync(http, "paused=traffic")).StatusCode.Should().Be(HttpStatusCode.OK, "the report is a report; the fields are simply not for this kind of peer");

        (await NodeOnMapAsync(id)).GetProperty("pausedText").ValueKind.Should().Be(JsonValueKind.Null);
        _factory.Services.GetRequiredService<PeerContactLog>().Get(id).PausedReason.Should().BeNull();
    }

    [Fact]
    public async Task APauseReportNobodyRenews_IsNotAnAnswer_ItLapsesAfterSevenDays()
    {
        var (id, http) = await PeerAsync();
        (await ReportAsync(http, "paused=traffic")).StatusCode.Should().Be(HttpStatusCode.OK);
        var log = _factory.Services.GetRequiredService<PeerContactLog>();

        // The same contact, eight days later: the view hides a pause report older than a week.
        var contact = log.Get(id);
        var row = new WhitelistEntry { NodeId = id, DisplayName = "Anna's phone", Ed25519PublicKey = new byte[32], Status = "A" };
        var now = DateTime.UtcNow;
        PeerContactView.Build(row, contact, null, null, 0, 0, now).PausedText.Should().NotBeNull();
        PeerContactView.Build(row, contact, null, null, 0, 0, now.AddDays(6)).PausedText.Should().NotBeNull();
        PeerContactView.Build(row, contact, null, null, 0, 0, now.AddDays(8)).PausedText.Should().BeNull();
    }

    // ── the alarm ─────────────────────────────────────────────────────────

    private static BlindNodeStatus Copy(DateTime now, TimeSpan quietFor, DeliveryNodeStatus? contact = null) =>
        new(BlindNodeId.NewId(), "Anna's phone", Address: null, Protocol: SyncProtocolVersion.Current, LastContact: now - quietFor, Alarms: [],
            CreatedAt: now - TimeSpan.FromDays(30), ProtocolSeenAt: now - quietFor, Contact: contact);

    private static DeliveryNodeStatus Contact(string? pausedReason, string? pausedText = null, DateTime? pausedUntil = null) =>
        new(Guid.NewGuid(), "Anna's phone", "private", 0, 0, 0, true, null, PausedReason: pausedReason, PausedUntil: pausedUntil, PausedText: pausedText);

    [Fact]
    public void ACopyThatWouldBeSilent_AndToldItIsPaused_IsPausedNotSilent()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var until = now.AddHours(10);
        var copy = Copy(now, TimeSpan.FromDays(5), Contact("traffic", "Paused by the device's owner: daily traffic limit", until));

        var alarms = BlindAlarmService.Evaluate(now, [copy], new Dictionary<Guid, DateTime>());

        var alarm = alarms.Should().ContainSingle().Which;
        alarm.Kind.Should().Be(BlindAlarmKinds.Paused);
        (alarm.NodeId, alarm.Banner, alarm.Notify, alarm.Since).Should().Be((copy.NodeId, true, false, until), "a pause is shown on the page and is never a notification");
    }

    [Fact]
    public void TheSameCopyWithNoPauseReport_IsSilentAsBefore()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var copy = Copy(now, TimeSpan.FromDays(5), Contact(null));

        var alarm = BlindAlarmService.Evaluate(now, [copy], new Dictionary<Guid, DateTime>()).Should().ContainSingle().Which;

        (alarm.Kind, alarm.Banner, alarm.Notify).Should().Be((BlindAlarmKinds.Silent, true, true));
    }

    [Fact]
    public void ACopyThatIsPaused_ButHeardFromLately_HasNoAlarmAtAll()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var copy = Copy(now, TimeSpan.FromHours(2), Contact("traffic", "Paused by the device's owner: daily traffic limit"));

        BlindAlarmService.Evaluate(now, [copy], new Dictionary<Guid, DateTime>()).Should().BeEmpty("paused replaces silent; it is not an alarm of its own");
    }

    [Fact]
    public void ARealOutOfDateProtocolAlarm_StaysWhileTheCopyIsPaused()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var copy = Copy(now, TimeSpan.FromDays(5), Contact("traffic", "Paused by the device's owner: daily traffic limit")) with { Protocol = 2 };

        BlindAlarmService.Evaluate(now, [copy], new Dictionary<Guid, DateTime>()).Select(a => a.Kind)
            .Should().BeEquivalentTo([BlindAlarmKinds.OldProtocol, BlindAlarmKinds.Paused]);
    }

    // ── leave ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenTheDevicesOwnerRemovesTheCopy_TheHubNotesIt_AndOnlyTheOwnerOfTheNetworkRevokes()
    {
        var (id, http) = await PeerAsync();

        (await http.PostAsync("/api/sync/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        Text(await NodeOnPageAsync(id), "leftAt").Should().NotBeNull();
        (await NodeOnMapAsync(id)).GetProperty("leftAt").ValueKind.Should().Be(JsonValueKind.String);
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!.Status.Should().Be("A", "the hub cannot revoke by itself");
        using (var conn = _factory.Services.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>().CreateConnection())
            Dapper.SqlMapper.ExecuteScalar<long>(conn, "SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'blind_copy_left' AND entity_id = @id", new { id = id.ToString() }).Should().Be(1);

        (await http.PostAsync("/api/sync/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent, "saying goodbye twice is the same goodbye");

        (await _admin.DeleteAsync($"/api/whitelist/{id}")).EnsureSuccessStatusCode();
        (await http.PostAsync("/api/sync/leave", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a revoked copy is cut off at once");
        (await _admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/", Web)).EnumerateArray().Should().NotContain(n => n.GetProperty("nodeId").GetGuid() == id);
    }

    [Fact]
    public async Task Leaving_NeedsASyncToken_AndOnlyABlindCopyLeavesThisWay()
    {
        var (full, fullHttp) = await PeerAsync(blind: false);

        (await _factory.Server.CreateClient().PostAsync("/api/sync/leave", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fullHttp.PostAsync("/api/sync/leave", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Services.GetRequiredService<PeerContactLog>().Get(full).LeftAt.Should().BeNull();
        (await _factory.Server.CreateClient().GetAsync("/api/sync/leave")).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task TheWebLayersDto_CarriesThePauseAndTheLeave_ToThePage()
    {
        var (paused, pausedHttp) = await PeerAsync();
        var (left, leftHttp) = await PeerAsync();
        (await ReportAsync(pausedHttp, "paused=disk")).EnsureSuccessStatusCode();
        (await leftHttp.PostAsync("/api/sync/leave", null)).EnsureSuccessStatusCode();

        var nodes = await new ApiClient(_admin).ListBlindNodesAsync();

        nodes!.Single(n => n.NodeId == paused).Contact!.PausedText.Should().Be("Paused by the device's owner: storage limit");
        nodes.Single(n => n.NodeId == left).Contact!.LeftAt.Should().NotBeNull();
        nodes.Single(n => n.NodeId == left).Contact!.PausedText.Should().BeNull();
    }
}
