using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// The backup schedule (plan §7): a daily backup at the configured UTC time, plus a weekly
/// <c>restic check --read-data-subset</c> after the Sunday backup. Every run time is jittered
/// ±15 minutes so a fleet of blind nodes on one LAN (or one S3 endpoint) does not stampede in
/// the same second, and so the schedule is not a precise clock an attacker with the backup
/// window could set their watch by.
/// </summary>
public sealed class BlindBackupScheduleService(
    BlindBackupSettingsStore settingsStore,
    BlindBackupService backups,
    BlindJobManager jobs,
    ILogger<BlindBackupScheduleService> logger) : BackgroundService
{
    private static readonly TimeSpan Jitter = TimeSpan.FromMinutes(15);
    // How late a missed slot is still caught up (the node was down or busy at 03:00 and came back
    // at 08:00). Beyond it the slot is skipped: enabling the schedule in the afternoon must not
    // start a backup the operator did not ask for right now.
    internal static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The scheduler ticks rather than sleeping to the next run: settings can change the time
        // while we sleep, and a 60s granularity of reaction to that is plenty.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = settingsStore.Load();
                // ScheduleRuns, not the raw flag: an undecided schedule is on once the node has a
                // repository and its password, so a node configured through init or the console
                // backs itself up without a second step. Saying so out loud the first time it
                // happens, because "backups are running" is worth knowing about even when the
                // operator never flipped the toggle.
                var runs = s.ScheduleRuns(settingsStore.DataPath);
                LogUndecidedOnce(s, runs);
                var slot = runs
                    ? DueSlot(s.ScheduleTime, DateTime.UtcNow, jobs.LastStartedAt("backup"), JitterOfTheDay)
                    : null;

                // A busy node keeps the slot due and retries next tick: the slot is only consumed
                // by a backup that actually started (LastStartedAt moves past it).
                if (slot is { } due && backups.TryStartBackup() is { } job)
                {
                    // The weekly subset check rides the Sunday backup (plan §7: subset on a
                    // schedule, full only on demand).
                    var wantCheck = due.DayOfWeek == DayOfWeek.Sunday;
                    logger.LogInformation("Blind schedule: backup {Id} for slot {Slot:u} started (check: {Check})",
                        job.Id, due, wantCheck);
                    if (wantCheck)
                    {
                        await job.Completion.WaitAsync(stoppingToken);
                        await StartWeeklyCheckAsync(stoppingToken);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Blind backup schedule cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
    }

    /// <summary>
    /// The slot a backup is owed for right now, or null. The most recent slot at or before
    /// <paramref name="now"/> (today's, or yesterday's while today's is still ahead) is owed
    /// unless a backup started at or after it, and only within <see cref="CatchUpWindow"/>.
    /// "Started after the slot" is read from the persisted job history, so a restart neither
    /// repeats a slot that already ran nor forgets one that has not.
    /// </summary>
    internal static DateTime? DueSlot(string scheduleTime, DateTime now, DateTime? lastBackupStartedAt,
        Func<DateTime, TimeSpan> jitterOfDay)
    {
        if (!TimeSpan.TryParse(scheduleTime, out var time)) return null;
        var slot = now.Date + time + jitterOfDay(now.Date);
        if (slot > now)
        {
            var yesterday = now.Date.AddDays(-1);
            slot = yesterday + time + jitterOfDay(yesterday);
        }
        if (lastBackupStartedAt >= slot) return null;
        if (now - slot > CatchUpWindow) return null;
        return slot;
    }

    private bool _saidUndecided;

    /// <summary>
    /// This node schedules backups nobody switched on. Said once per process, at Information — the
    /// console shows the schedule as on (that is what the node does), and this line is what tells
    /// an operator reading the log where that came from.
    /// </summary>
    private void LogUndecidedOnce(BlindBackupSettings s, bool runs)
    {
        if (_saidUndecided || !runs || s.ScheduleEnabled is not null) return;
        _saidUndecided = true;
        logger.LogInformation(
            "Blind schedule: a daily backup at {Time} UTC is on by default once the repository is "
            + "configured; turn the schedule off in the console (or in settings.json) to stop it",
            s.ScheduleTime);
    }

    // The jitter is per-day and remembered inside the day: a fresh random value on every tick
    // would move the slot each minute and could make it jump past "now" and back.
    private readonly Dictionary<DateTime, TimeSpan> _jitterByDay = [];

    private TimeSpan JitterOfTheDay(DateTime date)
    {
        if (_jitterByDay.TryGetValue(date, out var j)) return j;
        j = TimeSpan.FromSeconds(Random.Shared.Next(
            (int)-Jitter.TotalSeconds, (int)Jitter.TotalSeconds + 1));
        _jitterByDay[date] = j;
        if (_jitterByDay.Count > 7) _jitterByDay.Remove(_jitterByDay.Keys.Min());
        return j;
    }

    /// <summary>
    /// Starts the weekly subset check, waiting out whatever job holds the manager — an operator's
    /// "Back up now" right after the Sunday backup must delay the check, not cancel it for the
    /// week. Gives up only with the catch-up window, so a node that is never idle does not keep
    /// this loop alive into the next slot.
    /// </summary>
    internal async Task StartWeeklyCheckAsync(CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(CatchUpWindow);
        try
        {
            while (backups.TryStartVerify(full: false) is null)
                await jobs.WhenIdleAsync(window.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Blind schedule: the weekly check found no idle moment within {Window}", CatchUpWindow);
        }
    }
}
