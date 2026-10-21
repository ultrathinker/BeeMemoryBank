using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The timer that drops an abandoned seed upload (srvfix8, review RV6, tests the review named): the callback runs on the node's clock, drops an idle upload, cannot take the process down
/// whatever goes wrong inside it, and does nothing once the service is disposed. No test here sleeps: the sweep's own timer is fired by the test.
/// </summary>
public sealed class BlindSeedSweepTimerTests : IDisposable
{
    private const long MiB = 1L << 20;

    private readonly SweepClock _clock = new();
    private readonly Guid _seeder = Guid.NewGuid();
    private ClockedFactory? _node;

    public void Dispose() => _node?.Dispose();

    /// <summary>The real time moved by what a test adds; the timers of the sweep (and only those) are fired by the test, the rest run on the system's.</summary>
    private sealed class SweepClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<SweepTimer> _sweeps = [];
        private long _shift;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _shift, by.Ticks);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _shift));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != BlindSeedService.SweepEvery) return base.CreateTimer(callback, state, dueTime, period);
            var timer = new SweepTimer(callback, state);
            lock (_gate) _sweeps.Add(timer);
            return timer;
        }

        public int ArmedSweeps
        {
            get
            {
                lock (_gate) return _sweeps.Count(t => !t.Disposed);
            }
        }

        /// <summary>One tick of every sweep timer there is, including one that was taken down (a tick that was already on its way).</summary>
        public void FireSweeps()
        {
            SweepTimer[] timers;
            lock (_gate) timers = [.. _sweeps];
            foreach (var timer in timers) timer.Fire();
        }
    }

    private sealed class SweepTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A log sink that fails on the sweep's own warning - the failure happens inside the timer callback.</summary>
    private sealed class FailingLogger : ILogger<BlindSeedService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("was not continued")) throw new InvalidOperationException("the log sink failed");
        }
    }

    private sealed class ClockedFactory(TimeProvider clock, bool failingLog) : BlindNodeFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(clock);
                if (failingLog) services.AddSingleton<ILogger<BlindSeedService>>(new FailingLogger());
            });
        }
    }

    private async Task<(HttpClient Http, Guid SeedId)> NodeWithAnUploadAsync(bool failingLog = false)
    {
        _node = new ClockedFactory(_clock, failingLog);
        _ = _node.Services;
        var http = _node.Server.CreateClient();
        using var local = _node.CreateClient();
        var code = BlindPairCode.Parse((await local.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!);
        var seedId = Guid.NewGuid();
        using var first = Part(code.Secret, seedId, offset: 0);
        (await http.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        StagingDirs().Should().ContainSingle("the upload has started");
        return (http, seedId);
    }

    private HttpRequestMessage Part(string secret, Guid seedId, long offset)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/blind/seed?seedId={seedId}&offset={offset}&total={64 * MiB}&sha256={new string('0', 64)}") { Content = new ByteArrayContent(new byte[MiB]) };
        request.Headers.Add(BlindSeederProof.NodeIdHeader, _seeder.ToString());
        request.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(new byte[32]));
        request.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(secret, seedId, _seeder, new byte[32]));
        return request;
    }

    private string[] StagingDirs() => Directory.Exists(Path.Combine(_node!.DataPath, "blind-tmp"))
        ? Directory.GetDirectories(Path.Combine(_node.DataPath, "blind-tmp"), "seed-*")
        : [];

    [Fact]
    public async Task The_sweep_timer_is_on_the_nodes_clock_and_drops_an_upload_nobody_continued_for_an_hour()
    {
        await NodeWithAnUploadAsync();
        _clock.ArmedSweeps.Should().Be(1, "the first upload armed the sweep");

        _clock.FireSweeps();
        StagingDirs().Should().ContainSingle("a tick inside the hour leaves the upload alone");

        _clock.Advance(TimeSpan.FromMinutes(61));
        _clock.FireSweeps();

        StagingDirs().Should().BeEmpty("the sender has been away for more than an hour: the tick dropped the upload and its staging");
    }

    [Fact]
    public async Task A_sweep_tick_that_fails_inside_does_not_escape_and_leaves_the_service_working()
    {
        await NodeWithAnUploadAsync(failingLog: true);
        _clock.Advance(TimeSpan.FromMinutes(61));

        var tick = () => _clock.FireSweeps();

        tick.Should().NotThrow("an exception that leaves a timer callback ends the process");
        // The service's gate was given back by the failed tick: a barrier (which takes the gate) is granted at once.
        var seeds = _node!.Services.GetRequiredService<BlindSeedService>();
        using var barrier = await seeds.QuiesceAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        barrier.Should().NotBeNull();
    }

    [Fact]
    public async Task Once_the_service_is_disposed_its_timer_is_down_and_a_tick_on_its_way_does_nothing()
    {
        await NodeWithAnUploadAsync();
        _node!.Services.GetRequiredService<BlindSeedService>().Dispose();
        _clock.Advance(TimeSpan.FromMinutes(61));

        _clock.ArmedSweeps.Should().Be(0, "disposal took the timer down");
        var late = () => _clock.FireSweeps();

        late.Should().NotThrow();
        StagingDirs().Should().ContainSingle("a service that has been disposed sweeps nothing");
    }
}
