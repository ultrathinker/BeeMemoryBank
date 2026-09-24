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
/// the same row straight back up and fail it in a tight loop. So a deferral schedules exactly one
/// delayed retry at a time, doubling the delay each attempt up to <see cref="MaxDelay"/>, and gives
/// up after <see cref="MaxAttempts"/> (the next unlock and the admin's manual Apply still work).
/// A successful apply resets the budget. In-memory by design: a restart ends the schedule, and the
/// unlock that must follow a restart retries the rotation anyway.
/// </para>
/// </summary>
public sealed class DeferredRotationRetry
{
    private int _attempts;
    private int _scheduled;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Automatic retries scheduled since the last successful apply.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>
    /// Schedules one delayed call of <paramref name="retry"/> unless one is already pending or the
    /// budget is spent. Returns whether a retry was scheduled.
    /// </summary>
    public bool Schedule(Func<Task> retry, ILogger? logger)
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            return false; // one outstanding retry at a time

        var attempt = Interlocked.Increment(ref _attempts);
        if (attempt > MaxAttempts)
        {
            Volatile.Write(ref _scheduled, 0);
            logger?.LogWarning(
                "Deferred DEK rotation: giving up automatic retries after {Attempts} attempts; it stays pending for the next unlock or a manual apply",
                MaxAttempts);
            return false;
        }

        var delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << Math.Min(attempt - 1, 30))));
        logger?.LogInformation("Deferred DEK rotation: automatic retry {Attempt}/{Max} in {Delay}", attempt, MaxAttempts, delay);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            Volatile.Write(ref _scheduled, 0);
            try
            {
                await retry();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Deferred DEK rotation: automatic retry failed");
            }
        });
        return true;
    }

    /// <summary>Called after a successful apply: the next deferral starts a fresh budget.</summary>
    public void Reset() => Interlocked.Exchange(ref _attempts, 0);
}
