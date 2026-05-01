using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Services;

public class SyncNotificationService
{
    private SyncScheduler? _scheduler;
    private readonly ISyncTrigger _syncTrigger;
    private readonly ILogger<SyncNotificationService> _logger;
    private bool _isForeground;

    public bool HasPendingUpdates { get; private set; }
    public event EventHandler? PendingUpdatesChanged;

    public SyncNotificationService(
        ISyncTrigger syncTrigger,
        ILogger<SyncNotificationService> logger)
    {
        _syncTrigger = syncTrigger;
        _logger = logger;
    }

    public void AttachScheduler(SyncScheduler scheduler)
    {
        if (_scheduler != null)
        {
            _scheduler.SyncCycleCompleted -= OnSyncCycleCompleted;
        }

        _scheduler = scheduler;
        _scheduler.SyncCycleCompleted += OnSyncCycleCompleted;
        SetForegroundMode(_isForeground);
    }

    private void OnSyncCycleCompleted(object? sender, SyncCycleResult e)
    {
        if (e.TotalApplied > 0)
        {
            _logger.LogInformation("New data received ({Count} events). Setting pending flag.", e.TotalApplied);
            HasPendingUpdates = true;
            MainThread.BeginInvokeOnMainThread(() => 
            {
                PendingUpdatesChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void ClearPendingUpdates()
    {
        if (HasPendingUpdates)
        {
            _logger.LogInformation("Pending updates cleared.");
            HasPendingUpdates = false;
            MainThread.BeginInvokeOnMainThread(() => 
            {
                PendingUpdatesChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void SetForegroundMode(bool isForeground)
    {
        _isForeground = isForeground;
        var newInterval = isForeground ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5);
        if (_scheduler != null && _scheduler.Interval != newInterval)
        {
            _logger.LogInformation("Sync interval changed to {Interval}", newInterval);
            _scheduler.Interval = newInterval;
            
            if (isForeground)
            {
                // Wake up immediately when entering foreground
                _syncTrigger.Signal();
            }
        }
    }
}
