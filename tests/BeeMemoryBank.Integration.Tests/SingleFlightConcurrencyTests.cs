using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-311 fix round (F1, F8, F9): the gate is proved with REAL concurrent requests and a latch that holds the work, not with a lease the test holds itself.
/// The filter is tried on a small host of its own (exactly one of many concurrent requests runs, the rest are refused at once; the lease comes back after an
/// exception, after the client went away, and after a background operation the handler handed it to), and the two routes the review named are tried
/// end to end: the network restore (it answers 202 and goes on in the background) and "Add a blind node".
/// </summary>
public sealed class SingleFlightConcurrencyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private sealed class Probe
    {
        public int Entered;
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Background = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Body(string? Name);

    /// <summary>A host with nothing but the gate and a few routes that take a lease and wait on a latch.</summary>
    private sealed class MiniHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }
        public Probe Probe { get; }
        public LongOperationGate Gate => _app.Services.GetRequiredService<LongOperationGate>();

        public static async Task<MiniHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var probe = new Probe();
            builder.Services.AddSingleton<LongOperationGate>().AddSingleton(probe);
            var app = builder.Build();

            // An unhandled exception is a 500 for the client, as on the real host; the test host would rethrow it into the client call.
            app.Use(async (ctx, next) =>
            {
                try { await next(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Response.StatusCode = 500; }
            });

            app.MapPost("/slow/{id}", async (string id, HttpContext ctx, Probe p) =>
            {
                Interlocked.Increment(ref p.Entered);
                await p.Release.Task.WaitAsync(ctx.RequestAborted);
                return Results.Ok();
            }).SingleFlight("slow:{id}", "The slow thing");

            app.MapPost("/boom/{id}", IResult (string id) => throw new InvalidOperationException("the handler failed"))
                .SingleFlight("boom:{id}", "The failing thing");

            // Starts work that outlives the request and hands it the lease.
            app.MapPost("/background/{id}", (string id, HttpContext ctx, Probe p) =>
            {
                var lease = ctx.DetachSingleFlightLease();
                _ = Task.Run(async () =>
                {
                    try { await p.Background.Task; }
                    finally { lease.Dispose(); }
                });
                return Results.Accepted();
            }).SingleFlight("background:{id}", "The background thing");

            // Fails BEFORE it hands the lease over: the request still owns it, and gives it back.
            app.MapPost("/early-failure/{id}", IResult (string id, HttpContext ctx) =>
            {
                throw new InvalidOperationException("failed before the work was started");
            }).SingleFlight("early:{id}", "The early failure");

            // A route without a gate: handing over a lease it does not have is harmless.
            app.MapPost("/ungated", (HttpContext ctx) =>
            {
                ctx.DetachSingleFlightLease().Dispose();
                return Results.Ok();
            });

            // The key is in the body; no name means this request is not a long one.
            app.MapPost("/selected", async (Body body, HttpContext ctx, Probe p) =>
            {
                Interlocked.Increment(ref p.Entered);
                await p.Release.Task.WaitAsync(ctx.RequestAborted);
                return Results.Ok();
            }).SingleFlight("selected:{name}", "The selected thing", ctx =>
                ctx.Arguments.OfType<Body>().FirstOrDefault() is { Name: { Length: > 0 } name } ? "selected:" + name : null);

            await app.StartAsync();
            return new MiniHost(app, probe);
        }

        private MiniHost(WebApplication app, Probe probe)
        {
            _app = app;
            Probe = probe;
            Client = app.GetTestClient();
        }

        public async ValueTask DisposeAsync()
        {
            Probe.Release.TrySetResult();
            Probe.Background.TrySetResult();
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        public Task<HttpResponseMessage> PostAsync(string url, CancellationToken ct = default) => Client.PostAsync(url, null, ct);

        public async Task UntilAsync(Func<bool> condition)
        {
            var until = DateTime.UtcNow + Limit;
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("the condition never came true");
                await Task.Delay(10);
            }
        }
    }

    // ---- the filter ----

    [Fact]
    public async Task One_of_many_concurrent_requests_runs_the_rest_are_refused_at_once_and_another_key_runs_next_to_it()
    {
        await using var host = await MiniHost.StartAsync();

        var first = host.PostAsync("/slow/a");
        await host.UntilAsync(() => host.Probe.Entered == 1);
        var others = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.PostAsync("/slow/a")));

        others.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict, "they are answered while the first is still working, not queued behind it");
        (await others[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("already_running");
        host.Probe.Entered.Should().Be(1, "exactly one handler ran");
        first.IsCompleted.Should().BeFalse();

        var nextTo = host.PostAsync("/slow/b");   // another resource: the same operation runs next to it
        await host.UntilAsync(() => host.Probe.Entered == 2);

        host.Probe.Release.SetResult();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        (await nextTo).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Gate.Running().Should().BeEmpty();
    }

    [Fact]
    public async Task The_lease_comes_back_when_the_handler_throws()
    {
        await using var host = await MiniHost.StartAsync();

        (await host.PostAsync("/boom/a")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        host.Gate.IsRunning("boom:a").Should().BeFalse();
        (await host.PostAsync("/boom/a")).StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the handler ran again; it was not refused as running");
    }

    [Fact]
    public async Task The_lease_comes_back_when_the_client_goes_away_and_the_work_ends_with_it()
    {
        await using var host = await MiniHost.StartAsync();
        using var gone = new CancellationTokenSource();

        var first = host.PostAsync("/slow/a", gone.Token);
        await host.UntilAsync(() => host.Probe.Entered == 1);
        gone.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        await host.UntilAsync(() => !host.Gate.IsRunning("slow:a"));   // the handler took the request's token: it ended, and the lease with it
        host.Probe.Release.SetResult();
        (await host.PostAsync("/slow/a")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_lease_handed_to_background_work_lasts_until_that_work_ends()
    {
        await using var host = await MiniHost.StartAsync();

        (await host.PostAsync("/background/a")).StatusCode.Should().Be(HttpStatusCode.Accepted);

        host.Gate.IsRunning("background:a").Should().BeTrue("the request has been answered, the work has not ended");
        (await host.PostAsync("/background/a")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await host.PostAsync("/background/b")).StatusCode.Should().Be(HttpStatusCode.Accepted, "another key is not held");

        host.Probe.Background.SetResult();
        await host.UntilAsync(() => host.Gate.Running().Count == 0);
        (await host.PostAsync("/background/a")).StatusCode.Should().Be(HttpStatusCode.Accepted, "the work is over: the next request is the next run");
    }

    [Fact]
    public async Task A_failure_before_the_lease_is_handed_over_gives_it_back_and_a_route_without_a_lease_may_ask_for_it()
    {
        await using var host = await MiniHost.StartAsync();

        (await host.PostAsync("/early-failure/a")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.Gate.IsRunning("early:a").Should().BeFalse();

        (await host.PostAsync("/ungated")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_key_taken_from_the_body_gates_the_same_resource_and_a_request_without_one_is_not_a_long_one()
    {
        await using var host = await MiniHost.StartAsync();
        static StringContent Json(string? name) => new(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json");

        var first = host.Client.PostAsync("/selected", Json("x"));
        await host.UntilAsync(() => host.Probe.Entered == 1);
        var same = await host.Client.PostAsync("/selected", Json("x"));
        var other = host.Client.PostAsync("/selected", Json("y"));
        var noKey = host.Client.PostAsync("/selected", Json(null));
        var noKeyToo = host.Client.PostAsync("/selected", Json(""));
        await host.UntilAsync(() => host.Probe.Entered == 4);   // x, y and the two that carry no key all run

        same.StatusCode.Should().Be(HttpStatusCode.Conflict);
        host.Gate.Running().Select(o => o.Key).Should().BeEquivalentTo(["selected:x", "selected:y"]);
        host.Probe.Release.SetResult();
        (await Task.WhenAll(first, other, noKey, noKeyToo)).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_gate_says_what_is_running_and_since_when()
    {
        await using var host = await MiniHost.StartAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var first = host.PostAsync("/slow/a");
        await host.UntilAsync(() => host.Probe.Entered == 1);

        var running = host.Gate.Running().Should().ContainSingle().Subject;
        running.Key.Should().Be("slow:a");
        running.What.Should().Be("The slow thing");
        running.Since.Should().BeAfter(before);
        host.Probe.Release.SetResult();
        await first;
    }

    // ---- the routes the review named, end to end ----

    /// <summary>The restore this test holds: <see cref="RestoreInitiatorService.AcceptRestoreAsync"/> waits for a latch instead of restoring.</summary>
    private sealed class HeldRestore(
        SnapshotService snapshotService, IServiceScopeFactory scopeFactory, SessionService sessionService, IHttpClientFactory httpClientFactory,
        BeeMemoryBank.Storage.Sqlite.DbConnectionFactory connFactory, BeeMemoryBank.Core.Services.MaintenanceModeService maintenance,
        Microsoft.Extensions.Logging.ILogger<RestoreInitiatorService> logger, string dataPath, Probe probe)
        : RestoreInitiatorService(snapshotService, scopeFactory, sessionService, httpClientFactory, connFactory, maintenance, logger, dataPath)
    {
        public override async Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref probe.Entered);
            await probe.Background.Task;
        }
    }

    private sealed class HeldRestoreNode(Probe probe) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RestoreInitiatorService>();
                services.AddSingleton<RestoreInitiatorService>(sp => ActivatorUtilities.CreateInstance<HeldRestore>(sp, DataPath, probe));
            });
        }
    }

    [Collection(HeavyOperationCollection.Name)]
    public sealed class NetworkRestore : IAsyncLifetime
    {
        private const string Password = "singleFlightRestorePw";
        private readonly Probe _probe = new();
        private HeldRestoreNode _node = null!;
        private HttpClient _client = null!;

        public async Task InitializeAsync()
        {
            _node = new HeldRestoreNode(_probe);
            _client = _node.CreateClient();
            await _node.InitializeEncryptedNodeAsync("RestoreNode", Password);
        }

        public Task DisposeAsync()
        {
            _probe.Background.TrySetResult();
            _client.Dispose();
            _node.Dispose();
            return Task.CompletedTask;
        }

        private async Task<int> EventCountAsync()
        {
            using var scope = _node.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetTotalCountAsync();
        }

        private Task<HttpResponseMessage> RestoreAsync(string fileName) =>
            _client.PostAsync("/api/snapshots/restore-network", new StringContent(
                JsonSerializer.Serialize(new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide", masterPassword = Password }), Encoding.UTF8, "application/json"));

        [Fact]
        public async Task A_second_network_restore_while_the_first_is_still_running_in_the_background_is_refused_before_it_appends_an_event()
        {
            var snapshot = await _client.PostAsync("/api/snapshots", null);
            snapshot.EnsureSuccessStatusCode();
            var fileName = (await snapshot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;
            var gate = _node.Services.GetRequiredService<LongOperationGate>();

            var first = await RestoreAsync(fileName);
            first.StatusCode.Should().Be(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
            var afterFirst = await EventCountAsync();

            var second = await RestoreAsync(fileName);

            second.StatusCode.Should().Be(HttpStatusCode.Conflict, "the 202 of the first one did not end the restore: its lease is still held");
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("already_running");
            (await EventCountAsync()).Should().Be(afterFirst, "the second request appended no restore event and distributed nothing");
            gate.IsRunning("snapshot-restore").Should().BeTrue();

            _probe.Background.SetResult();   // the background restore ends
            var until = DateTime.UtcNow + Limit;
            while (gate.IsRunning("snapshot-restore") && DateTime.UtcNow < until) await Task.Delay(20);
            gate.IsRunning("snapshot-restore").Should().BeFalse("the lease went back when the background work was over");

            var third = await RestoreAsync(fileName);
            third.StatusCode.Should().Be(HttpStatusCode.Accepted, "the first restore is over: this is a new one");
            (await EventCountAsync()).Should().BeGreaterThan(afterFirst);
        }

        [Fact]
        public async Task A_restore_the_node_is_running_already_is_refused_by_the_initiation_check_too()
        {
            // A restore accepted from a peer holds no lease of this route, only the service's own lock: the check of the route sees it as well.
            var service = _node.Services.GetRequiredService<RestoreInitiatorService>();
            var held = (SemaphoreSlim)typeof(RestoreInitiatorService).GetField("_executeLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
            (await service.InitiationRefusalAsync()).Should().BeNull("nothing runs");

            await held.WaitAsync();
            try { (await service.InitiationRefusalAsync()).Should().Contain("already running"); }
            finally { held.Release(); }
            (await service.InitiationRefusalAsync()).Should().BeNull();
        }
    }

    private sealed class LatchedPeer(Probe probe) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref probe.Entered);
            await probe.Release.Task.WaitAsync(ct);
            throw new HttpRequestException("no blind node in this test");
        }
    }

    [Fact]
    public async Task Adding_a_blind_node_twice_at_once_runs_one_call_to_the_node_and_refuses_the_other()
    {
        var probe = new Probe();
        using var node = new BmbWebApplicationFactory();
        node.RouteOutboundHttpThrough(new LatchedPeer(probe));
        await node.InitializeNodeAsync("Node", "singleFlightAddPw");
        using var api = node.CreateClient();
        var code = new BlindPairCode(BlindNodeId.NewId(), Convert.ToBase64String(new byte[32]), "https://blind.test:5610", Convert.ToBase64String(new byte[32]),
            "secret", DateTime.UtcNow.AddMinutes(10)).ToString();

        var first = api.PostAsJsonAsync("/api/blind-nodes/", new { code });
        var until = DateTime.UtcNow + Limit;
        while (Volatile.Read(ref probe.Entered) == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        var second = await api.PostAsJsonAsync("/api/blind-nodes/", new { code });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("already_running");
        probe.Entered.Should().Be(1, "only the first request reached out to the blind node");
        probe.Release.SetResult();
        (await first).StatusCode.Should().Be(HttpStatusCode.BadGateway, "the first one ran to its end: the node it was to reach is not there");
        node.Services.GetRequiredService<LongOperationGate>().IsRunning("blind-add").Should().BeFalse();
    }
}
