using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The traffic cap at the door (srvfix6, R4-03): admission reserves, the reservation is settled with what really moved, and the route matrix, chunked bodies, ranges and
/// the days around a clock change are pinned. The middleware is driven as it runs in the node, with a stand-in for the handler.
/// </summary>
public sealed class BlindUsageAdmissionTests : IDisposable
{
    private const long MiB = 1L << 20;

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    public BlindUsageAdmissionTests() => Directory.CreateDirectory(_dataPath);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    private sealed class Clock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override TimeZoneInfo LocalTimeZone => zone;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private BlindUsageGuard NewGuard(long? maxTraffic = null, long? maxStored = null, TimeProvider? time = null)
    {
        var clock = time ?? _clock;
        var settings = new BlindBackupSettingsStore(_dataPath);
        settings.Save(new BlindBackupSettings { MaxStoredBytes = maxStored, MaxTrafficPerDay = maxTraffic });
        return new BlindUsageGuard(settings, new BlindUsageLedger(Path.Combine(_dataPath, "blind", "usage.json"), clock), new BlindStoredSize(_dataPath, clock), clock);
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

    /// <summary>A body of <paramref name="length"/> bytes that is not backed by memory.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Take(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Take(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Take(buffer.AsSpan(offset, count)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, _left);
            buffer[..n].Clear();
            _left -= n;
            return n;
        }
    }

    /// <summary>What a handler does right after it has authenticated its caller: reserve what the request is planned to move, or answer with the pause. False when it answered.</summary>
    private static async Task<bool> PeerAsync(HttpContext context)
    {
        if (context.ReserveForPeer() is not { } pause) return true;
        await BlindPauseResult.WriteAsync(context.Response, pause);
        return false;
    }

    /// <summary>Starts <paramref name="count"/> requests together; the stand-in handler of an admitted one waits for <paramref name="release"/>. Returns when each one is either held in the handler or refused.</summary>
    private static async Task<(int Admitted, int Refused, Task All)> RunTogetherAsync(int count, Func<int, DefaultHttpContext> makeRequest, Task release)
    {
        var admitted = 0;
        var refused = 0;
        var middleware = new BlindUsageMiddleware(async context =>
        {
            if (!await PeerAsync(context)) return;
            Interlocked.Increment(ref admitted);
            await release;
        });
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            var context = makeRequest(i);
            await middleware.InvokeAsync(context);
            if (context.Response.StatusCode == StatusCodes.Status503ServiceUnavailable) Interlocked.Increment(ref refused);
        })).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref admitted) + Volatile.Read(ref refused) < count && DateTime.UtcNow < deadline) await Task.Delay(10);
        return (Volatile.Read(ref admitted), Volatile.Read(ref refused), Task.WhenAll(tasks));
    }

    // ── R4-03: concurrent admission ───────────────────────────────────────

    [Fact]
    public async Task R4_03_concurrent_requests_cannot_all_pass_against_the_one_budget_that_is_left()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(19 * MiB);
        guard.FlushTraffic();
        var release = new TaskCompletionSource();

        var (admitted, refused, all) = await RunTogetherAsync(24, _ => Request(guard, "POST", "/api/sync/blobs", contentLength: MiB), release.Task);
        release.SetResult();
        await all;

        admitted.Should().Be(1, "one MiB is left of the day, and only one announced MiB fits in it");
        refused.Should().Be(23);
    }

    [Fact]
    public async Task R4_03_requests_of_unknown_size_reserve_a_minimum_each_and_only_that_many_fit()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(17 * MiB);
        guard.FlushTraffic();
        var release = new TaskCompletionSource();

        // The documented minimum for a body whose length nobody announced is 1 MiB: three MiB are left, so three of them are admitted.
        var (admitted, refused, all) = await RunTogetherAsync(20, _ => Request(guard, "POST", "/api/sync/events", contentLength: null), release.Task);
        release.SetResult();
        await all;

        admitted.Should().Be(3);
        refused.Should().Be(17);
    }

    [Fact]
    public async Task R4_03_what_an_admitted_request_moves_beyond_its_reservation_holds_back_the_next_one_while_it_is_still_running()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(5 * MiB);
        guard.FlushTraffic();
        var moved = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var middleware = new BlindUsageMiddleware(async context =>
        {
            if (!await PeerAsync(context)) return;
            var buffer = new byte[64 * 1024];
            while (await context.Request.Body.ReadAsync(buffer) > 0) { }
            moved.SetResult();
            await release.Task;
        });

        var first = Task.Run(() => middleware.InvokeAsync(Request(guard, "POST", "/api/sync/events", contentLength: null, new ZeroStream(12 * MiB))));
        await moved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = Request(guard, "POST", "/api/sync/blobs", contentLength: 4 * MiB);
        await new BlindUsageMiddleware(_ => Task.CompletedTask).InvokeAsync(second);
        release.SetResult();
        await first;

        second.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "5 MiB used and 12 MiB moving leave less than 4 MiB of the 20");
        second.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be(BlindPause.Busy, "the day is not used up; the 12 MiB on their way are what leaves no room");
        guard.TrafficToday().Should().Be(17 * MiB, "settled with what really moved");
    }

    [Fact]
    public async Task R4_03_a_download_that_announces_its_length_reserves_it_while_it_runs()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(5 * MiB);
        guard.FlushTraffic();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var download = new BlindUsageMiddleware(async context =>
        {
            context.Response.ContentLength = 15 * MiB;
            await context.Response.Body.WriteAsync(new byte[64 * 1024]);
            started.SetResult();
            await release.Task;
        });

        var running = Task.Run(() => download.InvokeAsync(Request(guard, "GET", "/api/blind/replica", contentLength: null)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var other = Request(guard, "GET", "/api/blind/restore/package", contentLength: null);
        await new BlindUsageMiddleware(async context => await PeerAsync(context)).InvokeAsync(other);
        release.SetResult();
        await running;

        other.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "5 MiB used and 15 announced leave nothing for a third request");
    }

    [Fact]
    public async Task R4_03_a_handler_that_throws_or_is_cancelled_leaves_no_reservation_behind()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        guard.CountTraffic(19 * MiB);
        guard.FlushTraffic();

        var failing = new BlindUsageMiddleware(context =>
        {
            context.ReserveForPeer();
            throw new InvalidOperationException("the handler failed");
        });
        var cancelled = new BlindUsageMiddleware(context =>
        {
            context.ReserveForPeer();
            throw new OperationCanceledException();
        });
        for (var i = 0; i < 5; i++)
        {
            await FluentActions.Awaiting(() => failing.InvokeAsync(Request(guard, "POST", "/api/sync/blobs", MiB))).Should().ThrowAsync<InvalidOperationException>();
            await FluentActions.Awaiting(() => cancelled.InvokeAsync(Request(guard, "GET", "/api/blind/replica", null))).Should().ThrowAsync<OperationCanceledException>();
        }

        var reached = false;
        var next = Request(guard, "POST", "/api/sync/blobs", MiB);
        await new BlindUsageMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(next);
        reached.Should().BeTrue("ten requests that ended badly must not have kept the last MiB of the day reserved");
        guard.TrafficToday().Should().Be(19 * MiB, "nothing moved in them");
    }

    [Fact]
    public async Task R4_03_a_cancelled_download_counts_what_it_wrote_and_gives_the_rest_back()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var aborted = new BlindUsageMiddleware(async context =>
        {
            context.Response.ContentLength = 15 * MiB;
            await context.Response.Body.WriteAsync(new byte[MiB]);
            throw new OperationCanceledException();
        });

        await FluentActions.Awaiting(() => aborted.InvokeAsync(Request(guard, "GET", "/api/blind/replica", null))).Should().ThrowAsync<OperationCanceledException>();

        guard.TrafficToday().Should().Be(MiB);
        var next = Request(guard, "POST", "/api/sync/blobs", 15 * MiB);
        var reached = false;
        await new BlindUsageMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(next);
        reached.Should().BeTrue("the 14 MiB that were announced and never sent are not held");
    }

    [Fact]
    public async Task A_range_request_is_counted_by_the_bytes_of_the_range()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var handler = new BlindUsageMiddleware(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentLength = 100;
            await context.Response.Body.WriteAsync(new byte[100]);
        });
        var context = Request(guard, "GET", "/api/blind/replica", null);
        context.Request.Headers.Range = "bytes=500-599";

        await handler.InvokeAsync(context);

        guard.TrafficToday().Should().Be(100, "the whole resource was not sent");
    }

    [Theory]
    [InlineData("HEAD", "/api/blind/replica")]
    [InlineData("HEAD", "/api/sync/events")]
    [InlineData("HEAD", "/api/blind/restore/package")]
    public async Task A_HEAD_request_is_not_metered_and_is_not_held_by_a_pause(string method, string path)
    {
        var guard = NewGuard(maxTraffic: 0);
        var reached = false;
        var context = Request(guard, method, path, null);

        await new BlindUsageMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context);

        reached.Should().BeTrue("a HEAD request has no body in either direction; the routes below answer it 405");
        guard.TrafficToday().Should().Be(0);
    }

    [Fact]
    public async Task A_head_to_a_data_route_of_the_running_node_moves_nothing_and_costs_nothing()
    {
        using var node = new BeeMemoryBank.Integration.Tests.BlindNodeFactory();
        _ = node.Services;
        using var client = node.CreateClient();
        var guard = node.Services.GetRequiredService<BlindUsageGuard>();

        foreach (var path in new[] { "/api/blind/replica", "/api/sync/events", "/api/blind/restore/package" })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
            response.IsSuccessStatusCode.Should().BeFalse($"{path} has no HEAD handler");
        }
        guard.TrafficToday().Should().Be(0);
    }

    // ── the route matrix ──────────────────────────────────────────────────

    public static TheoryData<string, string, bool> Matrix() => new()
    {
        // The data surface: metered, so a paused node answers them 503.
        { "POST", "/api/sync/events", true },
        { "POST", "/api/sync/blobs", true },
        { "POST", "/api/sync/blobs/check", true },
        { "POST", "/api/blind/seed", true },
        { "GET", "/api/sync/events", true },
        { "POST", "/api/sync/blobs/get", true },
        { "GET", "/api/blind/replica", true },
        { "GET", "/api/blind/restore/package", true },
        { "GET", "/api/blind/restore/events", true },
        { "POST", "/api/blind/claim", true },
        // Spelling that must not route around a pause: trailing slash, upper case.
        { "POST", "/api/sync/events/", true },
        { "GET", "/API/BLIND/REPLICA", true },
        // Exempt by design: a paused node still says who it is, lets peers authenticate and report, and lets its owner lift the pause.
        { "GET", "/api/sync/identity", false },
        { "POST", "/api/sync/challenge", false },
        { "POST", "/api/sync/authenticate", false },
        { "POST", "/api/sync/report-position", false },
        { "GET", "/api/blind/seed/6f0c7a3e-1c2b-4c0e-9d7e-3f5d4e7a9b10", false },
        { "GET", "/api/blind/status", false },
        { "GET", "/api/blind/pair-code", false },
        { "POST", "/api/blind/join", false },
        { "POST", "/api/blind/wipe", false },
        { "GET", "/health", false },
        // The same path with the other method is another route.
        { "GET", "/api/sync/blobs", false },
        { "POST", "/api/blind/replica", false },
        { "DELETE", "/api/sync/events", false },
        { "PUT", "/api/blind/seed", false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_route_matrix_says_which_routes_a_pause_stops_and_which_it_leaves_alone(string method, string path, bool metered)
    {
        var guard = NewGuard(maxTraffic: 0);
        var reached = false;
        var context = Request(guard, method, path, null);

        await new BlindUsageMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context);

        reached.Should().Be(!metered);
        if (metered)
        {
            context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
            context.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be(BlindPause.Traffic);
            context.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task A_chunked_body_is_counted_by_the_bytes_that_really_arrived()
    {
        var guard = NewGuard(maxTraffic: 20 * MiB);
        var middleware = new BlindUsageMiddleware(async context =>
        {
            var buffer = new byte[8192];
            while (await context.Request.Body.ReadAsync(buffer) > 0) { }
            await context.Response.WriteAsync("ok");
        });

        await middleware.InvokeAsync(Request(guard, "POST", "/api/sync/blobs", contentLength: null, new ZeroStream(3 * MiB + 17)));

        guard.TrafficToday().Should().Be(3 * MiB + 17 + 2);
    }

    // ── the days around a clock change ────────────────────────────────────

    private static TimeZoneInfo ZoneWithRepeatedMidnight()
    {
        // UTC-3; summer time begins at 00:00 standard (01:00 follows) on 4 November and ends at 01:00 summer (00:00 follows) on 17 February: the local midnight that begins
        // 17 February happens twice, and the one that begins 4 November does not happen at all.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 4),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 2, 17));
        return TimeZoneInfo.CreateCustomTimeZone("Test/RepeatedMidnight", TimeSpan.FromHours(-3), "repeated midnight", "standard", "summer", [rule]);
    }

    private static TimeZoneInfo ZoneAhead() => TimeZoneInfo.CreateCustomTimeZone("Test/Plus14", TimeSpan.FromHours(14), "plus 14", "plus 14");

    public static TheoryData<string> Zones() => new() { "repeated", "ahead", "utc" };

    private static TimeZoneInfo ZoneNamed(string name) => name switch { "repeated" => ZoneWithRepeatedMidnight(), "ahead" => ZoneAhead(), _ => TimeZoneInfo.Utc };

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_next_local_midnight_is_the_very_instant_the_day_turns_over(string zoneName)
    {
        var zone = ZoneNamed(zoneName);
        var instants = new[]
        {
            new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero),   // the day before the missing midnight
            new DateTimeOffset(2026, 11, 3, 23, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 16, 12, 0, 0, TimeSpan.Zero),   // the day before the repeated midnight
            new DateTimeOffset(2027, 2, 17, 2, 30, 0, TimeSpan.Zero),   // inside the repeated hour
            new DateTimeOffset(2027, 2, 17, 3, 30, 0, TimeSpan.Zero),   // after it
            new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero),  // an ordinary day
        };
        foreach (var start in instants)
        {
            var clock = new Clock(start, zone);
            var ledger = new BlindUsageLedger(Path.Combine(_dataPath, $"usage-{zoneName}-{start.ToUnixTimeSeconds()}.json"), clock);
            var today = ledger.Today();

            var turnover = ledger.NextLocalMidnight();

            turnover.Should().BeAfter(start, $"{zoneName} from {start:u}");
            clock.Now = turnover - TimeSpan.FromSeconds(1);
            ledger.Today().Should().Be(today, $"{zoneName}: a second before the turnover {turnover:u} it is still the same day");
            clock.Now = turnover;
            ledger.Today().Should().Be(today.AddDays(1), $"{zoneName}: at the turnover {turnover:u} the new day has begun");
        }
    }

    [Fact]
    public void A_traffic_pause_lifts_at_the_retry_after_the_answer_names_in_a_zone_that_repeats_its_midnight()
    {
        var zone = ZoneWithRepeatedMidnight();
        var clock = new Clock(new DateTimeOffset(2027, 2, 16, 20, 0, 0, TimeSpan.Zero), zone);
        var guard = NewGuard(maxTraffic: 10 * MiB, time: clock);
        guard.CountTraffic(10 * MiB);
        guard.FlushTraffic();
        var pause = guard.Check();
        pause.Should().NotBeNull();

        // The day only moves forward, so the second before is looked at first.
        clock.Now += pause!.RetryAfter - TimeSpan.FromSeconds(1);
        guard.Check().Should().NotBeNull("not a second earlier than the full node was told");
        clock.Now += TimeSpan.FromSeconds(1);
        guard.Check().Should().BeNull("the node answers again at the very moment it told the full node to come back");
    }
}
