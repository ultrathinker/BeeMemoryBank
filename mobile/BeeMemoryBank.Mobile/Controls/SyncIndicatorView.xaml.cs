using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Controls;

public partial class SyncIndicatorView : ContentView
{
    private Animation? _animation;
    private bool _isAnimating;
    private SyncNotificationService? _syncNotify;

    public SyncIndicatorView()
    {
        InitializeComponent();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null)
        {
            _syncNotify = IPlatformApplication.Current!.Services.GetRequiredService<SyncNotificationService>();
            _syncNotify.PendingUpdatesChanged += OnPendingUpdatesChanged;
            UpdateState();
        }
        else if (_syncNotify != null)
        {
            _syncNotify.PendingUpdatesChanged -= OnPendingUpdatesChanged;
            _syncNotify = null;
        }
    }

    private void OnPendingUpdatesChanged(object? sender, EventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        if (_syncNotify?.HasPendingUpdates == true)
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    private void StartAnimation()
    {
        if (_isAnimating) return;
        _isAnimating = true;

        _animation = new Animation(v => Dot.Opacity = v, 0.2, 1.0);
        _animation.Commit(this, "Pulse", length: 1500, easing: Easing.CubicInOut, repeat: () => true);
    }

    private void StopAnimation()
    {
        this.AbortAnimation("Pulse");
        Dot.Opacity = 0;
        _isAnimating = false;
    }

    private void OnIndicatorTapped(object? sender, EventArgs e)
    {
        _syncNotify?.ClearPendingUpdates();
    }
}
