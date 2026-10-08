using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>This node, the peers it syncs with and how the last contact with each went, and Sync now.</summary>
public partial class SyncPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly FullSync _sync;
    private readonly FullVault _vault;

    public SyncPage(IServiceProvider services, FullSync sync, FullVault vault)
    {
        InitializeComponent();
        _services = services;
        _sync = sync;
        _vault = vault;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _sync.Changed += OnChanged;
        await RefreshAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _sync.Changed -= OnChanged;
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(() => _ = RefreshAsync());

    private async void OnSyncNow(object? sender, EventArgs e)
    {
        SyncButton.IsEnabled = false;
        try { await _sync.RoundAsync(); }
        finally { SyncButton.IsEnabled = true; }
    }

    private async Task RefreshAsync()
    {
        var identity = await _vault.IdentityAsync();
        NodeLabel.Text = identity?.DisplayName ?? "";
        NodeIdLabel.Text = identity is null ? "" : "Node " + identity.NodeId;
        int count;
        using (var scope = _services.CreateScope())
            count = (await scope.ServiceProvider.GetRequiredService<IArticleRepository>().ListAsync()).Count;
        CountLabel.Text = count == 1 ? "1 note on this iPhone" : $"{count} notes on this iPhone";

        Busy.IsVisible = Busy.IsRunning = _sync.IsRunning;
        var round = _sync.LastRound;
        RoundLabel.Text = round is null
            ? (_sync.IsRunning ? "Syncing…" : "Not yet since the app opened.")
            : $"{round.At.ToLocalTime():HH:mm:ss}: {round.Reached} reached, {round.Failed} failed, {round.Applied} changes received";
        ProblemLabel.Text = round?.Problem;
        ProblemLabel.IsVisible = round?.Problem is not null;

        var peers = await _sync.PeersAsync();
        BindableLayout.SetItemsSource(PeersList, peers);
        NoPeersLabel.IsVisible = peers.Count == 0;
    }
}
