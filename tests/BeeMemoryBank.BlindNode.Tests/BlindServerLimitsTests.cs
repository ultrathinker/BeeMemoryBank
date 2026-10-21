using System.Text;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The server blind node's local limits: their durable local-day accounting and the gate that
/// keeps an over-limit request from reaching a route that could write its replica.
/// </summary>
public sealed class BlindServerLimitsTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));

    public BlindServerLimitsTests() => Directory.CreateDirectory(_dataPath);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public void EnvironmentLimitsRefuseNonsenseAndAcceptZeroAsAPause()
    {
        var bad = BlindNodeLimits.FromEnvironment(name => name == BlindNodeLimits.TrafficEnvName ? "1024" : null);
        bad.Limits.Should().BeNull();
        bad.Problem.Should().Contain("10 MB");

        var paused = BlindNodeLimits.FromEnvironment(name => name == BlindNodeLimits.StoredEnvName ? "0" : "");
        paused.Problem.Should().BeNull();
        paused.Limits.Should().Be(new BlindNodeLimits(0, null));
    }

    [Fact]
    public async Task EnvironmentLimitsAreAdoptedAtHostStartup()
    {
        using var node = new ConfiguredBlindNodeFactory(BlindNodeLimits.StoredEnvName, "4096");
        await node.InitializeNodeAsync();

        node.Services.GetRequiredService<BlindBackupSettingsStore>().Load().MaxStoredBytes.Should().Be(4096);
    }

    [Theory]
    [InlineData(BlindNodeLimits.StoredEnvName, "not-a-size")]
    [InlineData(BlindNodeLimits.TrafficEnvName, "1024")]
    public void InvalidEnvironmentLimitsRefuseHostStartup(string name, string value)
    {
        using var node = new ConfiguredBlindNodeFactory(name, value);

        FluentActions.Invoking(() => _ = node.Services)
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(name);
    }

    [Fact]
    public void LedgerSurvivesRestartAndAClockSetBackDoesNotRefillTheDay()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(_dataPath, "blind", "usage.json");
        var first = new BlindUsageLedger(path, clock);
        first.Add(40);
        first.Flush();

        clock.Now = clock.Now.AddHours(2);
        var afterMidnight = new BlindUsageLedger(path, clock);
        afterMidnight.Add(60);
        afterMidnight.Flush();

        clock.Now = clock.Now.AddDays(-1);
        var afterClockSetBack = new BlindUsageLedger(path, clock);

        afterClockSetBack.Today().Should().Be(new DateOnly(2026, 10, 22));
        afterClockSetBack.UsageToday().Should().Be(60);
    }

    [Fact]
    public void TrafficLimitLiftsAtTheNextLocalMidnight()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 21, 23, 55, 0, TimeSpan.Zero));
        var guard = CreateGuard(clock, maxTraffic: BlindNodeLimits.MinTrafficPerDay);

        guard.CountTraffic(BlindNodeLimits.MinTrafficPerDay);
        guard.FlushTraffic();
        guard.Check().Should().Match<BlindPause>(p => p.Reason == BlindPause.Traffic);

        clock.Now = clock.Now.AddMinutes(10);
        guard.Check().Should().BeNull();
    }

    [Fact]
    public async Task StoragePauseStopsADataReadBeforeTheRouteRuns()
    {
        var guard = CreateGuard(new MutableTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)), maxStored: 0);
        var services = new ServiceCollection().AddSingleton(guard).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/sync/events";
        context.Response.Body = new MemoryStream();
        var reachedRoute = false;
        var middleware = new BlindUsageMiddleware(_ =>
        {
            reachedRoute = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        reachedRoute.Should().BeFalse("a paused data route must not reach a handler that could read or write replica data");
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[PeerPausedException.HeaderName].ToString().Should().Be(BlindPause.Disk);
        context.Response.Headers.RetryAfter.ToString().Should().Be("3600");
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()).Should().Contain("disk");
    }

    private BlindUsageGuard CreateGuard(MutableTimeProvider clock, long? maxStored = null, long? maxTraffic = null)
    {
        var settings = new BlindBackupSettingsStore(_dataPath);
        settings.Save(new BlindBackupSettings { MaxStoredBytes = maxStored, MaxTrafficPerDay = maxTraffic });
        return new BlindUsageGuard(settings,
            new BlindUsageLedger(Path.Combine(_dataPath, "blind", "usage.json"), clock),
            new BlindStoredSize(_dataPath, clock), clock);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ConfiguredBlindNodeFactory(string name, string value) : BeeMemoryBank.Integration.Tests.BlindNodeFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting(name, value);
        }
    }
}
