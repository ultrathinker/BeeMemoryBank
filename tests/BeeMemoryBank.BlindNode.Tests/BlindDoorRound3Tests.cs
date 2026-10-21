using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The third round on the node's door (srvfix8, RV6-01, RV6-02): a body that three routes read before they know who is asking, and a request that outlives its reservation.
/// </summary>
public sealed class BlindDoorRound3Tests : IDisposable
{
    private const long KiB = 1024;
    private const long MiB = 1L << 20;

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));
    private readonly BlindNodeFactory _node = new();

    public BlindDoorRound3Tests() => Directory.CreateDirectory(_dataPath);

    public void Dispose()
    {
        _node.Dispose();
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    // ── RV6-01: no route reads an unbounded body before the credential is accepted ──

    /// <summary>A body that does not announce its length (a chunked request): the server cannot refuse it by the header, only by what it reads.</summary>
    private sealed class UnannouncedBody(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[8192];
            Array.Fill(chunk, (byte)'a');
            try
            {
                await stream.WriteAsync("{\"hashes\":[\""u8.ToArray());
                for (var left = length - 12; left > 0;)
                {
                    var n = (int)Math.Min(left, chunk.Length);
                    await stream.WriteAsync(chunk.AsMemory(0, n));
                    left -= n;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // The server answered without reading the rest, which is the right thing for it to do.
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static HttpContent Announced(long length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'a');
        "{\"hashes\":[\""u8.CopyTo(bytes);
        return new ByteArrayContent(bytes);
    }

    private async Task<string> PeerTokenAsync()
    {
        _ = _node.Services;
        var peer = Guid.NewGuid();
        using var scope = _node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "Phone", Ed25519PublicKey = new byte[32], Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return _node.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, SyncProtocolVersion.Current);
    }

    private async Task<(HttpStatusCode Status, long Counted)> PostAsync(string route, string credential, string token, bool announced, long size)
    {
        var guard = _node.Services.GetRequiredService<BlindUsageGuard>();
        var before = guard.TrafficToday();
        using var http = _node.Server.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = announced ? Announced(size) : new UnannouncedBody(size) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (credential == "peer") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (credential == "bad" && route.EndsWith("/claim")) request.Headers.Add(BlindRestoreEndpoints.RestoreCodeHeader, "not-a-code");
        else if (credential == "bad") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var response = await http.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        return (response.StatusCode, guard.TrafficToday() - before);
    }

    [Fact]
    public async Task RV6_01_the_three_routes_that_bind_a_body_never_read_more_than_their_limit_before_the_credential_is_accepted()
    {
        var token = await PeerTokenAsync();
        var problems = new List<string>();
        // The largest list a caller may send is 2000 hashes of 64 characters: about 134 KB of JSON. A claim is a few hundred bytes. The limits are 256 KiB and 16 KiB.
        var routes = new (string Route, string[] Credentials, long Limit)[]
        {
            ("/api/sync/blobs/check", ["none", "bad", "peer"], 256 * KiB),
            ("/api/sync/blobs/get", ["none", "bad", "peer"], 256 * KiB),
            ("/api/blind/claim", ["none", "bad"], 16 * KiB),
        };
        foreach (var (route, credentials, limit) in routes)
            foreach (var credential in credentials)
                foreach (var announced in new[] { true, false })
                {
                    var (status, counted) = await PostAsync(route, credential, token, announced, 1 * MiB);
                    var what = $"{route} credential={credential} {(announced ? "announced" : "chunked")}";
                    if (counted > limit + 4 * KiB) problems.Add($"{what}: {counted} bytes were read and counted (limit {limit})");
                    if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestEntityTooLarge)) problems.Add($"{what}: answered {(int)status}");
                    if (credential == "peer" && status != HttpStatusCode.RequestEntityTooLarge) problems.Add($"{what}: a valid peer's oversized body must be 413, was {(int)status}");
                }

        problems.Should().BeEmpty("a body is bounded by a small limit of its route before anything is bound, whoever sends it");
    }

    [Fact]
    public async Task RV6_01_a_valid_peers_normal_requests_on_those_routes_are_unchanged()
    {
        var token = await PeerTokenAsync();
        var hashes = Enumerable.Range(0, 2000).Select(i => i.ToString("x").PadLeft(64, '0')).ToList();
        using var http = _node.Server.CreateClient();

        foreach (var route in new[] { "/api/sync/blobs/check", "/api/sync/blobs/get" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(new { hashes }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, route + " with the largest list a caller may send");
        }
        using var tooMany = new HttpRequestMessage(HttpMethod.Post, "/api/sync/blobs/check") { Content = JsonContent.Create(new { hashes = hashes.Append("f".PadLeft(64, '0')).ToList() }) };
        tooMany.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var refused = await http.SendAsync(tooMany);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "2001 hashes are still the handler's own 400, not the limit's");
    }

    // ── RV6-02: a request that outlives its reservation does not go on unmetered ──

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    private BlindUsageGuard NewGuard(long? maxTraffic, long? maxStored = null)
    {
        var settings = new BlindBackupSettingsStore(_dataPath);
        settings.Save(new BlindBackupSettings { MaxStoredBytes = maxStored, MaxTrafficPerDay = maxTraffic });
        return new BlindUsageGuard(settings, new BlindUsageLedger(Path.Combine(_dataPath, "blind", "usage.json"), _clock), new BlindStoredSize(_dataPath, _clock), _clock);
    }

    /// <summary>A request body that gives a MiB for each permit the test releases, and may ignore the cancellation of the request like a handler that is stuck would.</summary>
    private sealed class PacedBody(int chunks, bool honoursCancellation) : Stream
    {
        private readonly SemaphoreSlim _permits = new(0);
        private int _served;

        public void Release(int count) => _permits.Release(count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= chunks) return 0;
            await _permits.WaitAsync(honoursCancellation ? cancellationToken : CancellationToken.None);
            _served++;
            var n = (int)Math.Min(buffer.Length, MiB);
            buffer.Span[..n].Clear();
            return n;
        }

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
    }

    private static DefaultHttpContext Post(BlindUsageGuard guard, long contentLength, Stream body, LifetimeFeature? lifetime = null)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(guard).BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.Path = "/api/sync/blobs";
        context.Request.ContentLength = contentLength;
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        if (lifetime is not null) context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        return context;
    }

    private static async Task<bool> PeerAsync(HttpContext context)
    {
        if (context.ReserveForPeer() is not { } pause) return true;
        await BlindPauseResult.WriteAsync(context.Response, pause);
        return false;
    }

    /// <summary>What a handler that reads its whole body does: reserve, then read a MiB at a time until the body ends or the read fails.</summary>
    private sealed class Reader(bool honoursAbort)
    {
        private long _received;
        private long _receivedAfterExpiry;
        private volatile bool _expired;
        public readonly TaskCompletionSource Reserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Received => Interlocked.Read(ref _received);
        public long ReceivedAfterExpiry => Interlocked.Read(ref _receivedAfterExpiry);
        public void Expired() => _expired = true;

        public async Task RunAsync(HttpContext context)
        {
            if (!await PeerAsync(context)) return;
            Reserved.TrySetResult();
            var buffer = new byte[MiB];
            while (true)
            {
                var n = await context.Request.Body.ReadAsync(buffer, honoursAbort ? context.RequestAborted : CancellationToken.None);
                if (n == 0) return;
                Interlocked.Add(ref _received, n);
                if (_expired) Interlocked.Add(ref _receivedAfterExpiry, n);
                FirstChunk.TrySetResult();
            }
        }
    }

    private static async Task EndedAsync(Task request)
    {
        try { await request.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or BadHttpRequestException) { }
    }

    [Fact]
    public async Task RV6_02_a_request_that_is_still_running_when_its_reservation_lifetime_ends_is_aborted()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var body = new PacedBody(chunks: 15, honoursCancellation: true);
        var lifetime = new LifetimeFeature();
        var reader = new Reader(honoursAbort: true);
        var first = Task.Run(() => new BlindUsageMiddleware(reader.RunAsync).InvokeAsync(Post(guard, 15 * MiB, body, lifetime)));
        try
        {
            await reader.Reserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            body.Release(1);
            await reader.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(30));

            reader.Expired();
            _clock.Advance(TimeSpan.FromMinutes(35));   // 30 minutes and the time 15 MiB take at 64 KiB/s (four minutes)

            lifetime.Aborted.Should().BeTrue("a request that takes longer than its reservation lasts is pathological: it is aborted, and the peer resumes as after any broken connection");
            await EndedAsync(first);

            var second = Post(guard, 10 * MiB, new MemoryStream(new byte[10 * MiB]));
            var secondReserved = false;
            await new BlindUsageMiddleware(async context =>
            {
                secondReserved = await PeerAsync(context);
                await context.Request.Body.CopyToAsync(Stream.Null);
            }).InvokeAsync(second);

            secondReserved.Should().BeTrue("the aborted request has ended and gave its room back");
            guard.TrafficToday().Should().BeLessThanOrEqualTo(20 * MiB, "the day does not pass its cap");
        }
        finally
        {
            body.Release(20);
        }
    }

    [Fact]
    public async Task RV6_02_a_request_that_ignores_the_abort_cannot_go_on_unmetered_and_the_day_does_not_pass_its_cap()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var body = new PacedBody(chunks: 15, honoursCancellation: false);
        var reader = new Reader(honoursAbort: false);
        var first = Task.Run(() => new BlindUsageMiddleware(reader.RunAsync).InvokeAsync(Post(guard, 15 * MiB, body, new LifetimeFeature())));
        try
        {
            await reader.Reserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            body.Release(1);
            await reader.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(30));

            reader.Expired();
            _clock.Advance(TimeSpan.FromMinutes(35));   // 30 minutes and the time 15 MiB take at 64 KiB/s (four minutes)

            // Another valid request now: whether it is admitted or told to come back, the day must not pass its cap.
            var second = Post(guard, 10 * MiB, new MemoryStream(new byte[10 * MiB]));
            await new BlindUsageMiddleware(async context =>
            {
                if (await PeerAsync(context)) await context.Request.Body.CopyToAsync(Stream.Null);
            }).InvokeAsync(second);

            body.Release(20);
            await EndedAsync(first);

            reader.ReceivedAfterExpiry.Should().Be(0, "after the lifetime ended not one more byte reaches the handler");
            guard.TrafficToday().Should().BeLessThanOrEqualTo(20 * MiB, "no request goes on uncounted past the lifetime: the day does not pass its cap");
        }
        finally
        {
            body.Release(20);
        }
    }

    [Fact]
    public async Task RV6_02_a_big_transfer_is_given_the_time_its_size_needs_at_a_minimum_rate_before_it_is_aborted()
    {
        // 600 MiB at 64 KiB/s is 160 minutes: a restore over a slow line is not cut at half an hour, a drip that announces the same is cut when its time is up.
        var guard = NewGuard(maxTraffic: 1024 * MiB);
        var body = new PacedBody(chunks: 600, honoursCancellation: true);
        var lifetime = new LifetimeFeature();
        var reader = new Reader(honoursAbort: true);
        var request = Task.Run(() => new BlindUsageMiddleware(reader.RunAsync).InvokeAsync(Post(guard, 600 * MiB, body, lifetime)));
        try
        {
            await reader.Reserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            body.Release(1);
            await reader.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(30));

            _clock.Advance(TimeSpan.FromMinutes(120));
            lifetime.Aborted.Should().BeFalse("two hours into a transfer that needs 190 minutes at the minimum rate it is not pathological");

            _clock.Advance(TimeSpan.FromMinutes(71));
            lifetime.Aborted.Should().BeTrue("30 minutes and 160 are over");
            await EndedAsync(request);
        }
        finally
        {
            body.Release(700);
        }
    }

    [Fact]
    public async Task RV7_02_ten_idle_authenticated_requests_that_hold_only_the_floor_are_aborted_at_the_lifetime_and_the_room_returns()
    {
        // A peer that announces one byte and sends nothing still holds a MiB of the cap (the floor): ten of them hold a cap of 10 MiB until they end.
        var guard = NewGuard(maxTraffic: 10 * MiB);
        var idle = new List<(Task Request, LifetimeFeature Lifetime, PacedBody Body)>();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var body = new PacedBody(chunks: 1, honoursCancellation: true);   // no permit is ever released: the peer sends nothing
                var lifetime = new LifetimeFeature();
                var reader = new Reader(honoursAbort: true);
                var request = Task.Run(() => new BlindUsageMiddleware(reader.RunAsync).InvokeAsync(Post(guard, 1, body, lifetime)));
                await reader.Reserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
                idle.Add((request, lifetime, body));
            }
            var refused = Post(guard, 1, new MemoryStream(new byte[1]));
            await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(refused);
            refused.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "the ten idle requests hold the whole cap");

            _clock.Advance(TimeSpan.FromMinutes(31));   // half an hour and the sixteen seconds a MiB takes at 64 KiB/s

            idle.Should().OnlyContain(r => r.Lifetime.Aborted, "a floor-sized request has a lifetime like any other");
            foreach (var (request, _, _) in idle) await EndedAsync(request);
            var again = Post(guard, 1, new MemoryStream(new byte[1]));
            var admitted = false;
            await new BlindUsageMiddleware(async context => admitted = await PeerAsync(context)).InvokeAsync(again);
            admitted.Should().BeTrue("the room the idle requests held went back when they ended");
        }
        finally
        {
            foreach (var (_, _, body) in idle) body.Release(5);
        }
    }

    [Fact]
    public async Task RV7_03_a_handler_stuck_after_the_abort_has_what_it_holds_settled_after_the_grace_and_what_it_moves_later_is_counted_once()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var lifetime = new LifetimeFeature();
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);   // waited on without a token: a call that does not return
        var first = Task.Run(async () =>
        {
            try
            {
                await new BlindUsageMiddleware(async context =>
                {
                    await PeerAsync(context);
                    await context.Response.Body.WriteAsync(new byte[MiB]);   // 1 MiB moved
                    reserved.SetResult();
                    await stuck.Task;
                    try { await context.Response.Body.WriteAsync(new byte[MiB / 2]); }   // moved after the grace: counted once, and refused
                    catch (IOException) { }
                }).InvokeAsync(Post(guard, 15 * MiB, new MemoryStream(), lifetime));
            }
            catch (IOException) { }
        });
        try
        {
            await reserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

            _clock.Advance(TimeSpan.FromMinutes(35));
            lifetime.Aborted.Should().BeTrue();
            var early = Post(guard, 10 * MiB, new MemoryStream(new byte[10 * MiB]));
            await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(early);
            early.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "inside the grace the stuck request still holds its 15 MiB");

            _clock.Advance(TimeSpan.FromMinutes(3));   // past the grace
            var late = Post(guard, 10 * MiB, new MemoryStream(new byte[10 * MiB]));
            var admitted = false;
            await new BlindUsageMiddleware(async context =>
            {
                admitted = await PeerAsync(context);
                if (admitted) await context.Request.Body.CopyToAsync(Stream.Null);
            }).InvokeAsync(late);
            admitted.Should().BeTrue("after the grace the stuck request holds nothing: what it moved is used, the rest of its reservation is back");

            stuck.SetResult();
            await EndedAsync(first);

            // 1 MiB the stuck request moved + 10 MiB of the second + the half MiB it moved after the grace: each exactly once.
            guard.TrafficToday().Should().BeInRange(11 * MiB + MiB / 2, 11 * MiB + MiB / 2 + 4096);
        }
        finally
        {
            stuck.TrySetResult();
        }
    }

    [Fact]
    public async Task RV7_03_the_storage_a_stuck_request_reserved_is_given_back_after_the_grace_too()
    {
        var guard = NewGuard(maxTraffic: null, maxStored: 12 * MiB);
        var lifetime = new LifetimeFeature();
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            try
            {
                await new BlindUsageMiddleware(async context =>
                {
                    await PeerAsync(context);
                    reserved.SetResult();
                    await stuck.Task;
                }).InvokeAsync(Post(guard, 10 * MiB, new MemoryStream(), lifetime));
            }
            catch (IOException) { }
        });
        try
        {
            await reserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _clock.Advance(TimeSpan.FromMinutes(33));   // past 30 minutes and the 160 seconds 10 MiB take at 64 KiB/s, inside the two-minute grace after the abort
            var early = Post(guard, 5 * MiB, new MemoryStream(new byte[5 * MiB]));
            await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(early);
            early.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "the 10 MiB it reserved are still held inside the grace");

            _clock.Advance(TimeSpan.FromMinutes(3));
            var late = Post(guard, 5 * MiB, new MemoryStream(new byte[5 * MiB]));
            var admitted = false;
            await new BlindUsageMiddleware(async context => admitted = await PeerAsync(context)).InvokeAsync(late);

            admitted.Should().BeTrue("past the grace the stuck request holds no storage");
        }
        finally
        {
            stuck.TrySetResult();
            await EndedAsync(first);
        }
    }

    [Fact]
    public async Task RV6_02_the_lifetime_timer_is_taken_down_when_the_request_ends_first()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var reader = new Reader(honoursAbort: true);
        var request = Post(guard, 3 * MiB, new MemoryStream(new byte[3 * MiB]), new LifetimeFeature());

        await new BlindUsageMiddleware(reader.RunAsync).InvokeAsync(request);

        reader.Received.Should().Be(3 * MiB);
        _clock.ArmedTimers.Should().Be(0, "a request that ended in time leaves nothing to fire");
    }
}
