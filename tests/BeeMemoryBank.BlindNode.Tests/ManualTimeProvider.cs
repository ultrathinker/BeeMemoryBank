using Microsoft.AspNetCore.Http.Features;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>The part of a server that ends a request: <see cref="Abort"/> is what <c>HttpContext.Abort()</c> calls, and it cancels <see cref="RequestAborted"/> like a dropped connection.</summary>
internal sealed class LifetimeFeature : IHttpRequestLifetimeFeature
{
    private readonly CancellationTokenSource _aborted = new();

    public bool Aborted { get; private set; }

    public CancellationToken RequestAborted { get => _aborted.Token; set => throw new NotSupportedException(); }

    public void Abort()
    {
        Aborted = true;
        _aborted.Cancel();
    }
}

/// <summary>
/// A clock that moves only when a test moves it, and whose timers fire only then: <see cref="Advance"/> runs, on the calling thread and in the order of their due times, every
/// timer that has come due. No test of a timer needs to sleep.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>The timers that are armed now (a test reads it to see that one was set, or taken down).</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.Armed);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Armed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due > _now) _now = next.Due;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Armed { get; private set; }

        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                Armed = dueTime != Timeout.InfiniteTimeSpan;
                Due = Armed ? _owner._now + dueTime : default;
                _period = period;
            }
            return true;
        }

        public void Fire()
        {
            lock (_owner._gate)
            {
                if (!Armed) return;
                if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero) Due += _period;
                else Armed = false;
            }
            _callback(_state);
        }

        public void Dispose()
        {
            lock (_owner._gate) Armed = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
