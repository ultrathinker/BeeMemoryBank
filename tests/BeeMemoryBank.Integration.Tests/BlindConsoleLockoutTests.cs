using BeeMemoryBank.Api.Services.BlindConsole;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The console lockout over time: it begins at the fifth bad password, ends at a fixed moment,
/// and probes during it change nothing — a local prober must not keep the owner out for ever.
/// </summary>
public class BlindConsoleLockoutTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_blind_lockout_" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BlindConsoleAuthService NewService() => new(_dir, NullLogger<BlindConsoleAuthService>.Instance, _clock);

    [Fact]
    public void ProbesDuringTheLock_DoNotExtendIt()
    {
        var auth = NewService();
        auth.TrySetPassword(null, "console-pw-123").Should().BeTrue();
        for (var i = 0; i < 5; i++)
        {
            auth.Verify("nope", "10.0.0.9");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        // A prober knocks every four minutes for the whole lock — with the RIGHT password even.
        for (var t = 0; t < 14; t += 4)
        {
            auth.Verify("console-pw-123", "10.0.0.9").Locked.Should().BeTrue($"still inside the lock at +{t} min");
            _clock.Advance(TimeSpan.FromMinutes(4));
        }

        _clock.Advance(TimeSpan.FromMinutes(1)); // 16 minutes after the lock began
        NewService().Verify("console-pw-123", "127.0.0.1").Should().Be((true, false),
            "the lock ends when it was set to end, however often it was probed (and a restart keeps that time)");
    }

    [Fact]
    public void AServedLock_StartsTheCountAgain()
    {
        var auth = NewService();
        auth.TrySetPassword(null, "console-pw-123");
        for (var i = 0; i < 5; i++) auth.Verify("nope", null);
        _clock.Advance(TimeSpan.FromMinutes(16));

        auth.Verify("nope", null).Locked.Should().BeFalse("one failure after a served lock is one failure, not the sixth");
    }
}
