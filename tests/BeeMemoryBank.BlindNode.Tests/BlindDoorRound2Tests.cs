using System.Net;
using System.Net.Http.Headers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The second round on the node's door (srvfix7, RV5-02, RV5-05, RV5-06): what a client that has not authenticated may hold, what an answer caused only by other requests'
/// reservations says, and a download that announces its length.
/// </summary>
public sealed class BlindDoorRound2Tests : IDisposable
{
    private const long MiB = 1L << 20;

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    public BlindDoorRound2Tests() => Directory.CreateDirectory(_dataPath);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    private BlindUsageGuard NewGuard(long? maxTraffic = null, long? maxStored = null)
    {
        var settings = new BlindBackupSettingsStore(_dataPath);
        settings.Save(new BlindBackupSettings { MaxStoredBytes = maxStored, MaxTrafficPerDay = maxTraffic });
        return new BlindUsageGuard(settings, new BlindUsageLedger(Path.Combine(_dataPath, "blind", "usage.json"), _clock), new BlindStoredSize(_dataPath, _clock), _clock);
    }

    private static DefaultHttpContext Request(BlindUsageGuard guard, string method, string path, long? contentLength, Stream? body = null)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(guard).BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.ContentLength = contentLength;
        context.Request.Body = body ?? new MemoryStream();
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>What a handler does right after it has authenticated its caller: reserve what the request is planned to move, or answer with the pause.</summary>
    private static async Task<bool> PeerAsync(HttpContext context)
    {
        if (context.ReserveForPeer() is not { } pause) return true;
        await BlindPauseResult.WriteAsync(context.Response, pause);
        return false;
    }

    // ── RV5-02: nobody who has not authenticated can hold the node's budget ──

    [Fact]
    public async Task RV5_02_twenty_unauthenticated_trickling_posts_do_not_stop_an_authenticated_peers_pull()
    {
        using var node = new BlindNodeFactory();
        _ = node.Services;
        node.Services.GetRequiredService<BlindBackupSettingsStore>().Update(s =>
        {
            s.MaxTrafficPerDay = 10 * MiB;
            return true;
        }).Should().BeTrue();
        var peer = Guid.NewGuid();
        using (var scope = node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = peer, DisplayName = "Phone", Ed25519PublicKey = new byte[32], Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        var token = node.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, SyncProtocolVersion.Current);

        // Twenty connections that announce half a MiB each and then trickle: the handler of this route reads the whole body before it looks at who is asking.
        var release = new TaskCompletionSource();
        var started = 0;
        var attackers = new List<Task>();
        using var attacker = node.Server.CreateClient();
        attacker.Timeout = TimeSpan.FromMinutes(5);
        for (var i = 0; i < 20; i++)
        {
            var content = new StreamContent(new TrickleStream(release.Task, () => Interlocked.Increment(ref started)));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentLength = MiB / 2;
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/sync/blobs/check") { Content = content };
            attackers.Add(Task.Run(async () =>
            {
                try { using var _ = await attacker.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); }
                catch (Exception) { /* released early at the end of the test */ }
            }));
        }
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref started) < 20 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(200);

        using var peerClient = node.Server.CreateClient();
        using var pull = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0&limit=10");
        pull.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var answer = await peerClient.SendAsync(pull);
        release.TrySetResult();
        await Task.WhenAll(attackers).WaitAsync(TimeSpan.FromSeconds(30));

        answer.StatusCode.Should().Be(HttpStatusCode.OK, "nothing that has not authenticated holds the day's budget");
    }

    private sealed class TrickleStream(Task release, Action started) : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                started();
                buffer.Span[0] = (byte)'{';
                return 1;
            }
            await release.WaitAsync(cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task RV5_02_a_request_that_has_not_authenticated_holds_nothing_and_one_that_has_holds_what_it_announced()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var release = new TaskCompletionSource();
        var inFlight = 0;
        var strangers = Enumerable.Range(0, 30).Select(_ => Task.Run(async () =>
        {
            var context = Request(guard, "POST", "/api/sync/blobs", contentLength: 5 * MiB);
            await new BlindUsageMiddleware(async _ =>
            {
                Interlocked.Increment(ref inFlight);
                await release.Task;
            }).InvokeAsync(context);
        })).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref inFlight) < 30 && DateTime.UtcNow < deadline) await Task.Delay(10);

        inFlight.Should().Be(30, "150 MiB announced against a 20 MiB day, and every one of them is in the handler: they hold nothing");
        var peer = Request(guard, "POST", "/api/sync/blobs", contentLength: 15 * MiB);
        var reserved = false;
        var peerHolds = new TaskCompletionSource();
        var peerTask = Task.Run(() => new BlindUsageMiddleware(async context =>
        {
            reserved = await PeerAsync(context);
            peerHolds.SetResult();
            await release.Task;
        }).InvokeAsync(peer));
        await peerHolds.Task.WaitAsync(TimeSpan.FromSeconds(30));
        reserved.Should().BeTrue("an authenticated peer's 15 MiB fit into the 20");
        var second = Request(guard, "POST", "/api/sync/blobs", contentLength: 6 * MiB);
        await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(second);
        release.SetResult();
        await Task.WhenAll(strangers.Append(peerTask));

        second.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "and what it holds is held against the next authenticated one");
    }

    [Fact]
    public async Task RV5_02_a_reservation_of_a_body_that_trickles_is_given_back_when_its_lifetime_ends_and_the_request_with_it()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var holding = new TaskCompletionSource();
        var lifetime = new LifetimeFeature();
        var trickle = Request(guard, "POST", "/api/sync/blobs", contentLength: 15 * MiB);
        trickle.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var trickling = Task.Run(async () =>
        {
            try
            {
                await new BlindUsageMiddleware(async context =>
                {
                    await PeerAsync(context);
                    holding.SetResult();
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }).InvokeAsync(trickle);
            }
            catch (OperationCanceledException)
            {
                // The abort the lifetime ended it with.
            }
        });
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var early = Request(guard, "POST", "/api/sync/blobs", contentLength: 10 * MiB);
        await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(early);
        early.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "while the 15 MiB are held");

        _clock.Advance(TimeSpan.FromMinutes(35));   // half an hour and the four minutes 15 MiB take at 64 KiB/s
        await trickling.WaitAsync(TimeSpan.FromSeconds(30));
        var late = Request(guard, "POST", "/api/sync/blobs", contentLength: 10 * MiB);
        var admitted = false;
        await new BlindUsageMiddleware(async context => admitted = await PeerAsync(context)).InvokeAsync(late);

        lifetime.Aborted.Should().BeTrue("the request that outlived its lifetime was aborted");
        admitted.Should().BeTrue("and the room it held went back with it");
    }

    [Fact]
    public async Task RV5_05_a_storage_refusal_that_rests_on_reservations_alone_is_busy_too()
    {
        var guard = NewGuard(maxStored: 20 * MiB);
        var held = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var big = Task.Run(() => new BlindUsageMiddleware(async context =>
        {
            if (!await PeerAsync(context)) return;
            held.SetResult();
            await release.Task;
        }).InvokeAsync(Request(guard, "POST", "/api/sync/events", contentLength: 15 * MiB)));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var other = Request(guard, "POST", "/api/sync/events", contentLength: 10 * MiB);
        await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(other);
        release.SetResult();
        await big;

        other.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        other.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be("busy");
        int.Parse(other.Response.Headers.RetryAfter.ToString()).Should().BeLessThanOrEqualTo(120);
    }

    [Fact]
    public async Task A_day_that_is_really_used_up_still_says_traffic_until_midnight()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(20 * MiB);
        guard.FlushTraffic();
        var context = Request(guard, "GET", "/api/sync/events", contentLength: null);

        await new BlindUsageMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        context.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be(BlindPause.Traffic);
        int.Parse(context.Response.Headers.RetryAfter.ToString()).Should().BeGreaterThan(3600);
    }

    // ── RV5-05: a refusal caused only by other requests' reservations is a short busy answer ──

    [Fact]
    public async Task RV5_05_a_request_refused_only_because_others_hold_the_budget_is_told_to_come_back_soon_not_after_midnight()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(5 * MiB);
        guard.FlushTraffic();
        var held = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var big = Task.Run(() => new BlindUsageMiddleware(async context =>
        {
            if (!await PeerAsync(context)) return;
            held.SetResult();
            await release.Task;
        }).InvokeAsync(Request(guard, "POST", "/api/sync/blobs", contentLength: 15 * MiB)));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var other = Request(guard, "GET", "/api/sync/events", contentLength: null);
        await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(other);
        release.SetResult();
        await big;

        other.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        other.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be("busy", "the day is not used up: another request holds the room, and may give it back in a minute");
        int.Parse(other.Response.Headers.RetryAfter.ToString()).Should().BeLessThanOrEqualTo(120);
    }

    // ── RV5-06: a download that announces its length is judged when it announces it ──

    [Fact]
    public async Task RV5_06_a_response_that_announces_more_than_the_day_has_left_is_refused_while_it_has_not_started()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(18 * MiB);
        guard.FlushTraffic();
        var download = new BlindUsageMiddleware(async context =>
        {
            context.Response.ContentLength = 500 * MiB;
            await context.Response.Body.WriteAsync(new byte[64 * 1024]);
            await context.Response.Body.WriteAsync(new byte[64 * 1024]);
        });
        var context = Request(guard, "GET", "/api/blind/replica", contentLength: null);

        await download.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "500 MiB do not fit into the 2 MiB the day has left");
        context.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be(BlindPause.Traffic);
        context.Response.ContentLength.Should().NotBe(500 * MiB);
        guard.TrafficToday().Should().BeLessThan(19 * MiB, "nothing of the announced 500 MiB was sent");
    }

    [Fact]
    public async Task RV5_06_a_response_that_does_not_fit_only_because_of_other_downloads_is_busy_and_does_not_start()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = Task.Run(() => new BlindUsageMiddleware(async context =>
        {
            context.Response.ContentLength = 15 * MiB;
            await context.Response.Body.WriteAsync(new byte[64 * 1024]);
            started.SetResult();
            await release.Task;
        }).InvokeAsync(Request(guard, "GET", "/api/blind/replica", contentLength: null)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = Request(guard, "GET", "/api/blind/replica", contentLength: null);
        await new BlindUsageMiddleware(async context =>
        {
            context.Response.ContentLength = 10 * MiB;
            await context.Response.Body.WriteAsync(new byte[64 * 1024]);
        }).InvokeAsync(second);
        release.SetResult();
        await first;

        second.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        second.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be("busy");
        guard.TrafficToday().Should().Be(64 * 1024, "the first download moved what it wrote; the second moved nothing");
    }

    // ── doubt: the body of a READ route is not stored ──

    [Fact]
    public async Task The_body_of_a_read_route_is_not_added_to_the_stored_reading()
    {
        var guard = NewGuard(maxStored: 100 * MiB);
        var before = guard.StoredNow().Bytes;
        var reads = new BlindUsageMiddleware(async context =>
        {
            var buffer = new byte[64 * 1024];
            while (await context.Request.Body.ReadAsync(buffer) > 0) { }
        });

        foreach (var path in new[] { "/api/sync/blobs/check", "/api/sync/blobs/get" })
            await reads.InvokeAsync(Request(guard, "POST", path, contentLength: MiB, new MemoryStream(new byte[MiB])));

        guard.StoredNow().Bytes.Should().Be(before, "a list of hashes to look up writes nothing");
    }
}
