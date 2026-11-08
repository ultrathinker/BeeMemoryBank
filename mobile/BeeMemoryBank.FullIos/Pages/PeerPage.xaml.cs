using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// One peer: how it is reached now, the addresses this iPhone has for it (its own, what it announced, where Bonjour showed it, what was typed here),
/// and "Add an address" for the case the others are wrong - a computer with a fixed address on a network Bonjour does not cross.
/// </summary>
public partial class PeerPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly FullSync _sync;
    private Guid _nodeId;
    private string _name = "";

    public PeerPage(IServiceProvider services, FullSync sync)
    {
        InitializeComponent();
        _services = services;
        _sync = sync;
    }

    /// <summary>Shows this peer.</summary>
    public PeerPage For(Guid nodeId, string name)
    {
        _nodeId = nodeId;
        _name = name;
        NameLabel.Text = name;
        return this;
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

    private async Task RefreshAsync()
    {
        try
        {
            var peer = (await _sync.PeersAsync()).FirstOrDefault(p => p.NodeId == _nodeId);
            StatusLabel.Text = peer is null ? "Not one of the devices this iPhone syncs with." : peer.ReachabilityText ?? peer.Summary;
            LocalNetworkLabel.Text = _sync.LocalNetworkNotice;
            LocalNetworkLabel.IsVisible = _sync.LocalNetworkNotice is not null;

            var channels = await PeerChannels.ListAsync(_services, _nodeId);
            BindableLayout.SetItemsSource(ChannelsList, channels);
            NoChannelsLabel.IsVisible = channels.Count == 0;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "The addresses could not be read: " + BusyAction.Describe(ex);
        }
    }

    private async void OnAddAddress(object? sender, EventArgs e)
    {
        var typed = await DisplayPromptAsync(
            "Add an address",
            $"An IP address, a name or a web address of {_name}. https:// and port {PeerChannels.DefaultLanPort} are filled in when left out.",
            "Check", "Cancel", "192.168.1.20", 200, Keyboard.Url);
        if (string.IsNullOrWhiteSpace(typed)) return;

        // The address is asked who it is before it is kept (a long wait when nothing answers): the one busy helper keeps the button from a second tap.
        await BusyAction.RunAsync(AddButton, async () =>
        {
            var result = await PeerChannels.AddAsync(_services, _nodeId, typed);
            if (result.CanSaveAnyway)
            {
                if (!await DisplayAlertAsync("Nothing to confirm it", result.Text + "\n\nSave the address anyway? Use it if the device is off or on another network now.", "Save anyway", "Cancel"))
                    return;
                result = await PeerChannels.AddAsync(_services, _nodeId, typed, saveWithoutAnswer: true);
            }
            if (!result.Succeeded)
            {
                await DisplayAlertAsync("Not saved", result.Text, "OK");
                return;
            }
            await RefreshAsync();
        }, new BusyOptions { FailureTitle = "Add an address" });
    }

    private async void OnRemove(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string address } button) return;
        if (!await DisplayAlertAsync("Remove the address", $"Stop using this address for {_name}? Other devices keep theirs.", "Remove", "Cancel")) return;
        await BusyAction.RunAsync(button, async () =>
        {
            await PeerChannels.RemoveAsync(_services, _nodeId, address);
            await RefreshAsync();
        }, new BusyOptions { FailureTitle = "Remove the address" });
    }
}
