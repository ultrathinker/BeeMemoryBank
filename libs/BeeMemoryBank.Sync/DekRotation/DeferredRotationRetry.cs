using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// Bounded, backed-off automatic retry for a peer rotation that was DEFERRED — a mandatory
/// pre-rewrap hook refused, nothing was changed, and the rotation stays Committing.
///
/// <para>
/// The unlock-time sweep is not enough on its own: an always-unlocked server never unlocks again,
/// so a transient refusal (a briefly locked host database) would leave it on the retired DEK until
/// someone noticed. The immediate post-apply sweep cannot be used for this either — it would pick
/// the same row straight back up and fail it in a tight loop. So a deferral schedules a delayed
/// retry, doubling the delay each attempt up to <see cref="MaxDelay"/>, and gives up after
/// <see cref="MaxAttempts"/> (the next unlock and the admin's manual Apply still work). A
/// successful apply resets the budget. In-memory by design: a restart ends the schedule, and the
/// unlock that must follow a restart retries the rotation anyway.
/// </para>
///
/// <para>
/// <b>One outstanding retry at a time</b>, counted from scheduling until the retry has FINISHED —
/// not merely until its delay elapsed. A <see cref="Schedule"/> call while a retry is pending or
/// running starts nothing and spends no attempt; it only records that another retry is wanted. When
/// the running retry finishes, that request (typically the retry's own deferral, which calls
/// <see cref="Schedule"/> from inside it) becomes the next scheduled attempt. So retries never run
/// concurrently, the budget counts real attempts only, and the chain still continues while the
/// rotation keeps being deferred.
/// </para>
/// </summary>
public sealed class DeferredRotationRetry
{
    private readonly object _gate = new();
    private int _attempts;
    private bool _outstanding;
    private Func<Task>? _requested;
    private ILogger? _requestedLogger;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Automatic retries scheduled since the last successful apply.</summary>
    public int Attempts
    {
        get { lock (_gate) return _attempts; }
    }

    /// <summary>
    /// Schedules one delayed call of <paramref name="retry"/> unless the budget is spent. If a
    /// retry is already pending or running, nothing starts now; the request is remembered and
    /// scheduled once that retry has finished. Returns whether a retry was scheduled now.
    /// </summary>
    public bool Schedule(Func<Task> retry, ILogger? logger)
    {
        TimeSpan delay;
        int attempt;
        lock (_gate)
        {
            if (_outstanding)
            {
                _requested = retry;
                _requestedLogger = logger;
                return false;
            }

            if (_attempts >= MaxAttempts)
            {
                logger?.LogWarning(
                    "Deferred DEK rotation: giving up automatic retries after {Attempts} attempts; it stays pending for the next unlock or a manual apply",
                    MaxAttempts);
                return false;
            }

            attempt = ++_attempts;
            _outstanding = true;
            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << Math.Min(attempt - 1, 30))));
        }

        logger?.LogInformation("Deferred DEK rotation: automatic retry {Attempt}/{Max} in {Delay}", attempt, MaxAttempts, delay);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await retry();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Deferred DEK rotation: automatic retry failed");
            }
            finally
            {
                Func<Task>? next;
                ILogger? nextLogger;
                lock (_gate)
                {
                    _outstanding = false;
                    next = _requested;
                    nextLogger = _requestedLogger;
                    _requested = null;
                    _requestedLogger = null;
                }
                if (next != null)
                    Schedule(next, nextLogger);
            }
        });
        return true;
    }

    /// <summary>Called after a successful apply: the next deferral starts a fresh budget.</summary>
    public void Reset()
    {
        lock (_gate) _attempts = 0;
    }
}
