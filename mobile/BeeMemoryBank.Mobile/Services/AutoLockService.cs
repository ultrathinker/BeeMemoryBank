using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// When the open vault closes by itself on this phone, built on the iPhone's shared <see cref="AutoLockPolicy"/>:
/// after the app has been away from the screen longer than the grace, and after the idle timeout on the
/// screen without a touch. The grace here is the mock-up's "lock when I leave the app" switch — on means
/// lock at once (the phone's long-standing behaviour), off means never lock on leaving. The idle timeout
/// is the mock-up's "lock when idle" choice. The choices are kept under the same preference keys the
/// iPhone uses. A touch anywhere in the window counts as activity (MainActivity forwards it).
/// </summary>
public sealed class AutoLockService
{
    private const string GraceKey = "lock.grace.seconds";
    private const string IdleKey = "lock.idle.seconds";

    /// <summary>A grace/idle this long never elapses in practice: the "don't lock" choice.</summary>
    public static readonly TimeSpan Never = TimeSpan.FromSeconds(int.MaxValue);

    private readonly AutoLockPolicy _policy = new();
    private readonly SessionService _session;
    private readonly IServiceProvider _services;
    private IDispatcherTimer? _idleTimer;

    /// <summary>A touch the window saw: the person is still here. MainActivity forwards every touch here.</summary>
    public static Action? Touch;

    public AutoLockService(SessionService session, IServiceProvider services)
    {
        _session = session;
        _services = services;
        _policy.BackgroundGrace = TimeSpan.FromSeconds(Preferences.Get(GraceKey, 0));
        _policy.IdleTimeout = TimeSpan.FromSeconds(Preferences.Get(IdleKey, 300));
        Touch = Activity;
    }

    public TimeSpan Grace
    {
        get => _policy.BackgroundGrace;
        set { _policy.BackgroundGrace = value; Preferences.Set(GraceKey, (int)value.TotalSeconds); }
    }

    public TimeSpan Idle
    {
        get => _policy.IdleTimeout;
        set { _policy.IdleTimeout = value; Preferences.Set(IdleKey, (int)value.TotalSeconds); }
    }

    /// <summary>The mock-up's switch: on = lock at once when leaving, off = never lock on leaving.</summary>
    public bool LockOnLeave
    {
        get => Grace == TimeSpan.Zero;
        set => Grace = value ? TimeSpan.Zero : Never;
    }

    public void Activity() => _policy.Activity(DateTime.UtcNow);

    /// <summary>The vault just opened and the app is in front: start counting idle time.</summary>
    public void Start()
    {
        _policy.Activity(DateTime.UtcNow);
        StartIdleTimer();
    }

    /// <summary>The app left the screen: lock at once when the grace is zero, else judge on the way back.</summary>
    public void OnBackground()
    {
        _policy.Left(DateTime.UtcNow);
        StopIdleTimer();
        if (!_session.IsUnlocked) return;
        if (_policy.BackgroundGrace == TimeSpan.Zero) LockVault();
    }

    /// <summary>Back on the screen: locked if the grace ran out, otherwise the idle count goes on.</summary>
    public void OnForeground()
    {
        if (!_session.IsUnlocked)
        {
            _policy.Returned(DateTime.UtcNow);
            return;
        }
        if (_policy.Returned(DateTime.UtcNow))
        {
            LockVault();
            return;
        }
        _policy.Activity(DateTime.UtcNow);
        StartIdleTimer();
    }

    /// <summary>The "Lock now" button: close the vault and stop the foreground sync with it.</summary>
    public void LockNow()
    {
        LockVault();
        App.StopSyncService();
    }

    // The same lock the app's own background path uses: on an encrypted phone the database closes with
    // it. The session's Locked event routes the shell to the unlock page (App.OnSessionLocked).
    private void LockVault()
    {
        if (_services.GetService<PhoneVaultLifecycle>() is { } lifecycle) _ = lifecycle.OnBackgroundAsync();
        else _session.Lock();
    }

    private void StartIdleTimer()
    {
        if (_idleTimer is not null || Application.Current is null) return;
        _idleTimer = Application.Current.Dispatcher.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(15);
        _idleTimer.Tick += (_, _) =>
        {
            if (_session.IsUnlocked && _policy.IsIdle(DateTime.UtcNow)) LockVault();
        };
        _idleTimer.Start();
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Stop();
        _idleTimer = null;
    }
}
