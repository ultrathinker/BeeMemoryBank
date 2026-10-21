using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Services;

/// <summary>
/// What one account's panel asks of the device loop (<see cref="IBlindWorkRequests"/>): "Sync now", "Back up now", the first load after pairing - for THIS account. The loop
/// (<see cref="BlindDeviceScheduler"/>) runs them one at a time with every other account's work, and tells the panel when a job it asked for has ended. Subscribing to
/// <see cref="UserJobReported"/> hooks the loop's event only while somebody listens.
/// </summary>
public sealed class AccountWorkRequests : IBlindWorkRequests
{
    private readonly BlindDeviceScheduler _scheduler;
    private readonly string _accountId;
    private readonly BlindDeviceSchedulerOptions _options;
    private readonly object _gate = new();
    private Action<string>? _handlers;

    public AccountWorkRequests(BlindDeviceScheduler scheduler, string accountId, BlindDeviceSchedulerOptions? options = null)
    {
        _scheduler = scheduler;
        _accountId = accountId;
        _options = options ?? new BlindDeviceSchedulerOptions();
    }

    public void RequestSync() => _scheduler.RequestSync(_accountId);

    public void RequestBackup() => _scheduler.RequestBackup(_accountId);

    public void RequestHeavy() => _scheduler.RequestHeavy(_accountId);

    public event Action<string>? UserJobReported
    {
        add
        {
            lock (_gate)
            {
                if (_handlers is null) _scheduler.UserJobReported += OnReported;
                _handlers += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _handlers -= value;
                if (_handlers is null) _scheduler.UserJobReported -= OnReported;
            }
        }
    }

    /// <summary>The next try of a first load that failed with every condition fine, from the loop's plan for this account; null when none is planned.</summary>
    public FirstLoadRetryInfo? FirstLoadRetry
    {
        get
        {
            if (_scheduler.ScheduleOf(_accountId) is not { HeavyRetryAt: { } nextAt, RetryDelay: { } delay } plan) return null;
            return new FirstLoadRetryInfo(nextAt, delay, plan.RetryAttempts, delay >= _options.FirstLoadRetryMax);
        }
    }

    private void OnReported(string accountId, string sentence)
    {
        if (accountId != _accountId) return;
        Action<string>? handlers;
        lock (_gate) handlers = _handlers;
        handlers?.Invoke(sentence);
    }
}
