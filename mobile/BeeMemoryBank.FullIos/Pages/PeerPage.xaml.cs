using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
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
    private readonly SecurityOps _security;
    private Guid _nodeId;
    private string _name = "";

    public PeerPage(IServiceProvider services, FullSync sync, SecurityOps security)
    {
        InitializeComponent();
        _services = services;
        _sync = sync;
        _security = security;
    }

    /// <summary>Shows this peer.</summary>
    public PeerPage For(Guid nodeId, string name)
    {
        _nodeId = nodeId;
        _name = name;
        NameLabel.Text = name;
        Title = name;
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
            if (peer is null)
            {
                StatusLabel.Text = AppStrings.Get("PeerNotOne");
            }
            else
            {
                var problem = Controls.SyncText.Problem(peer.ProblemCode, peer.Name);
                StatusLabel.Text = problem ?? Controls.SyncText.Reachability(peer.Reachability, peer.Name) ?? Controls.SyncText.Contact(peer);
                StatusLabel.TextColor = peer.ProblemCode == FullSync.ProblemUnreachable
                    ? ThemeManager.Token("Warning")
                    : peer.ProblemCode is not null && peer.ProblemCode != FullSync.ProblemDisconnected
                        ? ThemeManager.Token("Error") : ThemeManager.Token("TextSecondary");
            }
            LocalNetworkLabel.Text = _sync.LocalNetworkNotice is not null ? AppStrings.Get("SyncLanOff") : "";
            LocalNetworkLabel.IsVisible = _sync.LocalNetworkNotice is not null;

            var channels = await PeerChannels.ListAsync(_services, _nodeId);
            BindableLayout.SetItemsSource(ChannelsList,
                channels.Select(c => new Controls.PeerChannelDisplay(c.Address, Controls.SyncText.ChannelLine(c), c.ThisIPhoneOnly)).ToList());
            NoChannelsLabel.IsVisible = channels.Count == 0;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = AppStrings.Format("PeerReadError", BusyAction.Describe(ex));
        }
    }

    private async void OnAddAddress(object? sender, EventArgs e)
    {
        var typed = await this.ShowInputPopupAsync(
            AppStrings.Get("PeerAddTitle"),
            AppStrings.Format("PeerAddDesc", _name, PeerChannels.DefaultLanPort),
            AppStrings.Get("PeerCheck"), placeholder: "192.168.1.20", maxLength: 200);
        if (string.IsNullOrWhiteSpace(typed)) return;

        // The address is asked who it is before it is kept (a long wait when nothing answers): the one busy helper keeps the button from a second tap.
        await BusyAction.RunAsync(AddButton, async () =>
        {
            var result = await PeerChannels.AddAsync(_services, _nodeId, typed);
            if (result.CanSaveAnyway)
            {
                if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("PeerNothingTitle"),
                        Controls.SyncText.AddAddressText(result, BeeMemoryBank.Core.Models.BlindNodeId.IsBlind(_nodeId)) + "\n\n" + AppStrings.Get("PeerNothingDesc"),
                        AppStrings.Get("PeerSaveAnyway")))
                    return;
                result = await PeerChannels.AddAsync(_services, _nodeId, typed, saveWithoutAnswer: true);
            }
            if (!result.Succeeded)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("PeerNotSaved"),
                    Controls.SyncText.AddAddressText(result, BeeMemoryBank.Core.Models.BlindNodeId.IsBlind(_nodeId)));
                return;
            }
            await RefreshAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("PeerAddTitle") });
    }

    private async void OnRemove(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string address } button) return;
        if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("PeerRemoveTitle"), AppStrings.Format("PeerRemoveDesc", _name), AppStrings.Get("Remove"))) return;
        await BusyAction.RunAsync(button, async () =>
        {
            await PeerChannels.RemoveAsync(_services, _nodeId, address);
            await RefreshAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("PeerRemoveTitle") });
    }

    // Remove the device itself: log the revoke first, then stamp the whitelist row (the Android order).
    private async void OnRemoveDevice(object? sender, EventArgs e)
    {
        if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("DeviceRemoveDevice"),
                AppStrings.Format("RemoveDeviceDescFormat", _name), AppStrings.Get("Delete"), danger: true)) return;
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(() => _security.RevokePeerAsync(_nodeId));
            await Navigation.PopAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("DeviceRemoveDevice") });
    }

#if DEBUG || BMB_E2E
    /// <summary>The end-to-end check photographs the remove-device confirm left open.</summary>
    internal Task ShowRemoveConfirmForE2EAsync() => AppDialog.ConfirmAsync(this, AppStrings.Get("DeviceRemoveDevice"),
        AppStrings.Format("RemoveDeviceDescFormat", _name), AppStrings.Get("Delete"), danger: true);
#endif
}
