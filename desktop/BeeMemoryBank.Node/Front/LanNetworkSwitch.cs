using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Infrastructure.Network;

namespace BeeMemoryBank.Node;

/// <summary>
/// The settings of the LAN listener in the one place that orders them: "Devices on my network" (the web surface), "Sync with your devices on this
/// network" (the sync surface) and the LAN port. The listener first, the saved setting after, so a switch that could not open the listener is not
/// remembered as on; and the door of "Connect a device" closed before the web surface opens (its code would carry a token the open front does not ask
/// for) or the port moves (its code names the old port).
/// </summary>
public sealed class LanNetworkSwitch
{
    private readonly NodeNetworkSettingsStore _store;
    private readonly LanListener? _listener;
    private readonly LanJoinListener? _door;
    private readonly NetworkExposure _startup;

    // One switch at a time: the listener transition, the saved setting and a rollback are ONE operation. The listener has its own
    // gate, but that only orders its own open and close; two requests in flight could otherwise finish their saves in the opposite
    // order of their transitions and leave the file and the listener disagreeing (and the announcer with them) until the next start.
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <param name="store">The profile's saved settings.</param>
    /// <param name="listener">The node's LAN listener; null when <c>BMB_HTTPS_ENABLED=1</c> makes the front itself hold the port.</param>
    /// <param name="door">The temporary door, closed whenever the web surface opens or the port moves; null when there is none.</param>
    /// <param name="startup">What the node decided when it started: when that came from <c>BMB_HTTPS_ENABLED=1</c> the front itself
    /// holds the port and the settings cannot change anything.</param>
    public LanNetworkSwitch(NodeNetworkSettingsStore store, LanListener? listener, LanJoinListener? door, NetworkExposure startup)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _listener = listener;
        _door = door;
        _startup = startup;
        if (listener is null && startup.Source != NetworkExposureSource.Environment)
            throw new ArgumentNullException(nameof(listener), "Only the BMB_HTTPS_ENABLED front has no LAN listener.");
    }

    private bool ByEnvironment => _startup.Source == NetworkExposureSource.Environment;

    /// <summary>The web setting, or what overrides it: "off", "on" or "environment".</summary>
    public string Setting => ByEnvironment ? "environment" : (_store.Load().DevicesOnMyNetwork ? "on" : "off");

    /// <summary>True while the node answers the network with its whole front, by the setting or by <c>BMB_HTTPS_ENABLED=1</c>.</summary>
    public bool IsOn => ByEnvironment || _listener?.WebOn == true;

    /// <summary>The LAN sync setting: "on", "off", or "environment" (the BMB_HTTPS_ENABLED front serves everything, sync included).</summary>
    public string SyncSetting => ByEnvironment ? "environment" : (_store.Load().LanSync ? "on" : "off");

    /// <summary>The port every LAN surface uses now.</summary>
    public int Port => _listener?.Port ?? _store.CurrentPort();

    /// <summary>"setting" or "environment" (<c>BMB_LAN_PORT</c> decides for this run).</summary>
    public string PortSource => LanPorts.IsFromEnvironment(Environment.GetEnvironmentVariable(LanPorts.EnvironmentVariable)) ? "environment" : "setting";

    /// <summary>The listener, for the status (null under BMB_HTTPS_ENABLED=1).</summary>
    public LanListener? Listener => _listener;

    /// <summary>Node start: opens the surfaces the profile has on, and starts watching the networks. A failure is logged and leaves that surface closed.</summary>
    public async Task ApplyAtStartAsync(Action<string> log)
    {
        if (ByEnvironment || _listener is null) return;
        await _switchGate.WaitAsync();
        try
        {
            var settings = _store.Load();
            await _listener.SetSyncAsync(settings.LanSync);
            if (settings.DevicesOnMyNetwork)
            {
                try
                {
                    await _listener.SetWebAsync(true);
                    log("[Node] Devices on my network is ON: the front is also served over HTTPS on port " + _listener.Port + ".");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
                {
                    log($"[Node] WARNING: Devices on my network is set but the listener could not start ({ex.Message}); the node answers this computer only.");
                }
            }
            await _listener.StartAsync();
            log(settings.LanSync
                ? $"[Node] LAN sync is ON: paired devices reach this node on port {_listener.Port} on trusted networks ({string.Join(", ", _listener.SyncAddresses)})."
                : "[Node] LAN sync is OFF.");
        }
        finally { _switchGate.Release(); }
    }

    /// <summary>
    /// Switches the web surface and the saved setting together. Throws <see cref="LanNetworkException"/> when
    /// <c>BMB_HTTPS_ENABLED=1</c> decides, and <see cref="InvalidOperationException"/> / <see cref="IOException"/> when the
    /// listener cannot open (the setting stays as it was).
    /// </summary>
    public async Task SetAsync(bool enabled)
    {
        if (ByEnvironment || _listener is null)
            throw new LanNetworkException(
                $"{NetworkExposure.EnvironmentVariable}=1 is set for this node and switches the listener on by itself; remove the variable to control it here.");

        await _switchGate.WaitAsync();
        try
        {
            if (enabled)
            {
                if (_door != null) await _door.DisableAsync();
                await _listener.SetWebAsync(true);
                try { _store.Update(s => s with { DevicesOnMyNetwork = true }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await _listener.SetWebAsync(false);
                    throw new LanNetworkException($"The setting could not be saved: {ex.Message}");
                }
            }
            else
            {
                await _listener.SetWebAsync(false);
                try { _store.Update(s => s with { DevicesOnMyNetwork = false }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Closed for now, but it would come back at the next start: say so rather than pretend.
                    throw new LanNetworkException($"The listener is closed, but the setting could not be saved ({ex.Message}); it would open again at the next start.");
                }
            }
        }
        finally { _switchGate.Release(); }
    }

    /// <summary>Switches LAN sync ("Sync with your devices on this network") and saves it.</summary>
    public async Task SetSyncAsync(bool enabled)
    {
        if (ByEnvironment || _listener is null)
            throw new LanNetworkException(
                $"{NetworkExposure.EnvironmentVariable}=1 is set for this node: its front serves everything on the network; remove the variable to control LAN sync here.");
        await _switchGate.WaitAsync();
        try
        {
            await _listener.SetSyncAsync(enabled);
            try { _store.Update(s => s with { LanSync = enabled }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _listener.SetSyncAsync(!enabled);
                throw new LanNetworkException($"The setting could not be saved: {ex.Message}");
            }
        }
        finally { _switchGate.Release(); }
    }

    /// <summary>
    /// Moves every LAN surface to <paramref name="port"/> and saves it. The paired devices that run this version follow by themselves (this node announces
    /// its new endpoints, and its mDNS record carries the port); one of an older version keeps the address it has until it is updated or edited.
    /// </summary>
    public async Task SetPortAsync(int port)
    {
        if (ByEnvironment || _listener is null)
            throw new LanNetworkException($"{NetworkExposure.EnvironmentVariable}=1 is set for this node: its port is fixed for this run.");
        if (PortSource == "environment")
            throw new LanNetworkException($"{LanPorts.EnvironmentVariable} is set for this node and decides its port; remove the variable to change it here.");
        if (!LanPorts.IsAllowed(port)) throw new LanNetworkException(LanPorts.Refusal);
        await _switchGate.WaitAsync();
        try
        {
            var before = _listener.Port;
            if (before == port) return;
            if (_door != null) await _door.DisableAsync();
            _store.Update(s => s with { LanPort = port == LanPorts.Default ? null : port });
            await _listener.SetPortAsync(port);
            if (_listener.LastBindError is { } error && _listener.Bound.Count == 0 && (_listener.SyncOn || _listener.WebOn))
                throw new LanNetworkException($"The port is saved, but nothing could listen on it yet: {error}.");
        }
        finally { _switchGate.Release(); }
    }

    /// <summary>The one-time notice about LAN sync was read.</summary>
    public void DismissNotice() => _store.Update(s => s with { LanSyncNoticeSeen = true });

    /// <summary>True until the person dismissed the one-time notice "Direct sync with your devices on this network is on" (D9).</summary>
    public bool NoticePending => !ByEnvironment && _store.Load() is { LanSync: true, LanSyncNoticeSeen: false };
}

/// <summary>A refusal the person looking at the card can act on (not a fault of the node).</summary>
public sealed class LanNetworkException(string message) : Exception(message);
