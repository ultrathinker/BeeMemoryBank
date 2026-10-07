using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using BeeMemoryBank.Infrastructure.Network;

namespace BeeMemoryBank.Node;

/// <summary>
/// The permanent LAN listener behind the "Devices on my network" setting: the node's whole front (web UI and API, the
/// same routes the loopback front has) over HTTPS on every network interface, with the certificate of the node's own local CA.
/// It is a second Kestrel server inside the Node process, beside the loopback front, so it can be opened and closed while the
/// node runs: the Api and Web children are not touched and the vault stays unlocked. The loopback front is not touched either, so
/// the desktop shell and the page that is switching it never lose their connection.
///
/// <para>Unlike <see cref="LanJoinListener"/> (the temporary door of "Connect a device": four join routes, a one-time token) it
/// exposes everything the front serves, so anyone on the network can reach the sign-in page. That is what the setting is for,
/// and what the card in Admin says. The <c>/node</c> routes stay loopback-only and refuse a relayed request, as on the loopback
/// front (see <see cref="NodeFront.MapEndpoints"/>).</para>
/// </summary>
public sealed class LanFrontListener : IAsyncDisposable
{
    private readonly Func<WebApplication> _build;
    private readonly Func<X509Certificate2?> _certificate;
    private readonly Action<bool>? _stateChanged;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebApplication? _app;
    private bool _disposed;   // guarded by _gate

    /// <param name="build">Builds the (not yet started) server: <see cref="NodeFrontBuilder.BuildNetworkFront"/> on a real node.</param>
    /// <param name="certificate">The node's TLS leaf; only asked here whether there is one, so a node that cannot serve TLS refuses
    /// to switch on instead of listening with no certificate.</param>
    /// <param name="stateChanged">Told <c>true</c> once the listener really listens and <c>false</c> as soon as it stops, however it stops
    /// (switched off, disposed, or the server went down by itself). On a real node it writes <see cref="LanListenerState"/>, which the mDNS
    /// announcer reads: a record must follow the listener, not the setting, or a port that failed to bind would still be announced.</param>
    public LanFrontListener(Func<WebApplication> build, Func<X509Certificate2?> certificate, Action<bool>? stateChanged = null)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _stateChanged = stateChanged;
    }

    /// <summary>True while the listener is open.</summary>
    public bool IsOn => Volatile.Read(ref _app) != null;

    /// <summary>
    /// Opens the listener, or does nothing if it is open. Throws <see cref="InvalidOperationException"/> when the node has no TLS
    /// certificate to serve (a system without a place to keep the CA's key), and when the port is taken the server's own
    /// <see cref="IOException"/> / <see cref="InvalidOperationException"/> reaches the caller: nothing is left half open.
    /// </summary>
    public async Task EnableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_app != null) return;

            // Not disposed: the provider hands out the certificate it serves, which the TLS handshakes still use.
            if (_certificate() is null)
                throw new InvalidOperationException("This node has no TLS certificate to serve the network with.");

            var app = _build();
            try { await app.StartAsync(); }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
            Volatile.Write(ref _app, app);
            // A server that goes down by itself (not through CloseAsync, which has already emptied _app) is no longer listening:
            // say so, so the announcement is withdrawn instead of pointing at a closed port.
            app.Lifetime.ApplicationStopped.Register(() =>
            {
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _app, null, app), app)) return;
                Notify(false);
                _ = Task.Run(async () => { try { await app.DisposeAsync(); } catch { /* already down */ } });
            });
            Notify(true);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the listener. Safe to call when it is already closed.</summary>
    public async Task DisableAsync()
    {
        await _gate.WaitAsync();
        try { await CloseAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Behind the gate, so an enable that is opening the listener right now finishes first and is closed below,
        // and every enable after this one is refused: a late request must not open a network listener after the node
        // has closed it on its way out.
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await CloseAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task CloseAsync()
    {
        if (_app is not { } app) return;
        Volatile.Write(ref _app, null);
        Notify(false); // before the stop: the announcement goes first, the connections drain afterwards
        // Graceful, bounded: a download in flight is let finish, but a stuck connection cannot hold the switch.
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await app.StopAsync(stopCts.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }

    private void Notify(bool listening)
    {
        try { _stateChanged?.Invoke(listening); }
        catch { /* the state file is best effort; the listener itself must not fail for it */ }
    }
}

/// <summary>
/// What turning "Devices on my network" on or off does, in the one place that orders it: the listener first, the saved
/// setting after, so a switch that could not open the listener is not remembered as on; and the door of "Connect a
/// device" closed before the permanent listener takes its port.
/// </summary>
public sealed class LanNetworkSwitch
{
    private readonly NodeNetworkSettingsStore _store;
    private readonly LanFrontListener _listener;
    private readonly LanJoinListener? _door;
    private readonly NetworkExposure _startup;

    // One switch at a time: the listener transition, the saved setting and a rollback are ONE operation. The listener has its own
    // gate, but that only orders its own open and close; two requests in flight could otherwise finish their saves in the opposite
    // order of their transitions and leave the file and the listener disagreeing (and the announcer with them) until the next start.
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <param name="store">The profile's saved setting.</param>
    /// <param name="listener">The permanent listener the setting controls.</param>
    /// <param name="door">The temporary door, closed whenever the permanent listener opens (both want the same port); null when there is none.</param>
    /// <param name="startup">What the node decided when it started: when that came from <c>BMB_HTTPS_ENABLED=1</c> the front itself
    /// holds the port and the setting cannot change anything.</param>
    public LanNetworkSwitch(NodeNetworkSettingsStore store, LanFrontListener listener, LanJoinListener? door, NetworkExposure startup)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _door = door;
        _startup = startup;
    }

    /// <summary>The setting, or what overrides it: "off", "on" or "environment".</summary>
    public string Setting => _startup.Source == NetworkExposureSource.Environment
        ? "environment"
        : (_store.Load().DevicesOnMyNetwork ? "on" : "off");

    /// <summary>True while the node answers the network permanently, by the setting or by <c>BMB_HTTPS_ENABLED=1</c>.</summary>
    public bool IsOn => _startup.Source == NetworkExposureSource.Environment || _listener.IsOn;

    /// <summary>Node start: opens the listener if the profile has the setting on. A failure is logged and leaves the node closed.</summary>
    public async Task ApplyAtStartAsync(Action<string> log)
    {
        if (_startup.Source == NetworkExposureSource.Environment) return;
        await _switchGate.WaitAsync();
        try
        {
            if (!_store.Load().DevicesOnMyNetwork) return;
            try
            {
                await _listener.EnableAsync();
                log("[Node] Devices on my network is ON: the front is also served over HTTPS on port " + NodeFront.HttpsPort + ".");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                log($"[Node] WARNING: Devices on my network is set but the listener could not start ({ex.Message}); the node answers this computer only.");
            }
        }
        finally { _switchGate.Release(); }
    }

    /// <summary>
    /// Switches the listener and the saved setting together. Throws <see cref="LanNetworkException"/> when
    /// <c>BMB_HTTPS_ENABLED=1</c> decides, and <see cref="InvalidOperationException"/> / <see cref="IOException"/> when the
    /// listener cannot open (the setting stays as it was).
    /// </summary>
    public async Task SetAsync(bool enabled)
    {
        if (_startup.Source == NetworkExposureSource.Environment)
            throw new LanNetworkException(
                $"{NetworkExposure.EnvironmentVariable}=1 is set for this node and switches the listener on by itself; remove the variable to control it here.");

        await _switchGate.WaitAsync();
        try
        {
            if (enabled)
            {
                if (_door != null) await _door.DisableAsync();
                await _listener.EnableAsync();
                try { _store.Save(new NodeNetworkSettings(DevicesOnMyNetwork: true)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await _listener.DisableAsync();
                    throw new LanNetworkException($"The setting could not be saved: {ex.Message}");
                }
            }
            else
            {
                await _listener.DisableAsync();
                try { _store.Save(new NodeNetworkSettings(DevicesOnMyNetwork: false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Closed for now, but it would come back at the next start: say so rather than pretend.
                    throw new LanNetworkException($"The listener is closed, but the setting could not be saved ({ex.Message}); it would open again at the next start.");
                }
            }
        }
        finally { _switchGate.Release(); }
    }
}

/// <summary>A refusal the person looking at the card can act on (not a fault of the node).</summary>
public sealed class LanNetworkException(string message) : Exception(message);
