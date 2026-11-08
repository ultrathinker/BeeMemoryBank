using System.Net;
using System.Threading.Channels;
using Android.Content;
using Android.Net.Nsd;
using Android.Net.Wifi;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// The phone's look for its paired peers on the Wi-Fi (docs/design/lan-peer-sync.md sections 7 and 16): <c>NsdManager</c> browses
/// <c>_beememorybank._tcp</c>, every service it finds is resolved one at a time (Android before 14 allows one resolve in flight; from 14 a service-info
/// callback is used), and what <see cref="LanSighting"/> accepts - a paired node, <c>https=true</c>, an IPv4 address of a LAN or VPN - is noted in the
/// <see cref="PeerDiscoveryCache"/> the channel planner reads. It dials nothing and listens for nothing but the NSD answers.
/// <para>It runs only while the app's sync runs: <see cref="ScanAsync"/> before a sync round (the worker, Sync now), <see cref="RunAsync"/> for the life of
/// the foreground service (a short look every <see cref="Interval"/>, and at once when a peer's LAN addresses failed - a DHCP change). Between looks no
/// listener is registered and no multicast lock is held. No location or nearby-devices permission is needed for NSD; the multicast lock is a normal
/// permission, taken for the few seconds of a look so the Wi-Fi chip does not drop the answers while the screen is off.</para>
/// </summary>
public sealed class NsdPeerDiscovery(
    PeerDiscoveryCache cache, IServiceScopeFactory scopes, ILogger<NsdPeerDiscovery> logger) : ILanPeerScanner
{
    /// <summary>How often the foreground service looks again when nobody asked sooner.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a look lasts when every paired peer has not been seen sooner.</summary>
    public static TimeSpan ScanTime { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>The most one resolve waits: an NSD that never answers must not hold the queue.</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A look ends this long after the last service it found (or after its start when it found none): a network with no desktop on it costs no more than this.</summary>
    private static readonly TimeSpan QuietFor = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>One look, now. A look that is already running is waited for instead of starting another.</summary>
    public async Task ScanAsync(TimeSpan lookFor, CancellationToken ct)
    {
        try
        {
            if (!await _one.WaitAsync(0, ct))
            {
                await _one.WaitAsync(ct);
                _one.Release();
                return;
            }
            try { await LookAsync(lookFor, ct); }
            finally { _one.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A phone that cannot look still syncs over the addresses it has.
            logger.LogDebug(ex, "The look for peers on the Wi-Fi failed");
        }
    }

    /// <summary>Looks every <see cref="Interval"/> and at once when the sync loop asked for it, until <paramref name="ct"/> ends (the foreground service stops).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var next = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= next || cache.TakePrompt())
            {
                next = DateTime.UtcNow + Interval;
                await ScanAsync(ScanTime, ct);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task LookAsync(TimeSpan lookFor, CancellationToken ct)
    {
        var paired = await PairedAsync(ct);
        if (paired.Count == 0) return;
        var context = global::Android.App.Application.Context;
        if (context.GetSystemService(Context.NsdService) is not NsdManager nsd) return;

        using var multicast = MulticastLockOf(context);
        var noted = new HashSet<Guid>();
        var found = Channel.CreateBounded<NsdServiceInfo>(new BoundedChannelOptions(LanSighting.MaxServicesPerLook)
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true,
        });
        var listener = new Listener(found.Writer, logger);
        using var lookCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookCts.CancelAfter(lookFor);
        using var quiet = new CancellationTokenSource(QuietFor);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(lookCts.Token, quiet.Token);

        try
        {
            nsd.DiscoverServices(LanSighting.ServiceType, NsdProtocol.DnsSd, listener);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "NSD would not start a look");
            return;
        }

        try
        {
            var resolved = 0;
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var service in found.Reader.ReadAllAsync(reading.Token))
            {
                // The same service is announced once per interface; one resolve is enough.
                if (service.ServiceName is not { } name || !seenNames.Add(name)) continue;
                if (++resolved > LanSighting.MaxServicesPerLook) break;
                quiet.CancelAfter(ResolveTimeout + QuietFor);   // a slow resolve must not end the look under its own feet
                var info = await ResolveAsync(context, nsd, service, lookCts.Token);
                quiet.CancelAfter(QuietFor);
                if (info is null || Sighting(info, paired) is not { } seen) continue;
                LanSighting.Note(cache, seen, DateTime.UtcNow);
                noted.Add(seen.NodeId);
                if (noted.IsSupersetOf(paired)) break;   // everyone is seen: no reason to keep the radio busy
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { nsd.StopServiceDiscovery(listener); }
            catch (Exception ex) { logger.LogDebug(ex, "NSD would not stop a look (it had not started)"); }
        }
    }

    private async Task<IReadOnlySet<Guid>> PairedAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            // Invisible mode: other nodes do not see this phone and it does not sync, so it does not announce itself with a question on the Wi-Fi either.
            if (scope.ServiceProvider.GetService<InvisibleModeService>() is { IsInvisible: true }) return new HashSet<Guid>();
            var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            var peers = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
            ct.ThrowIfCancellationRequested();
            return peers.Where(p => p.NodeId != identity?.NodeId && !BlindNodeId.IsBlind(p.NodeId)).Select(p => p.NodeId).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A closed (encrypted, locked) database or a vault not ready: nothing to look for now.
            logger.LogDebug(ex, "The paired peers could not be read; no look for them");
            return new HashSet<Guid>();
        }
    }

    /// <summary>The wake-up for a multicast answer while the screen is off; null when the system will not hand one out.</summary>
    private static MulticastHold? MulticastLockOf(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.WifiService) is not WifiManager wifi) return null;
            var held = wifi.CreateMulticastLock("bmb-nsd");
            if (held is null) return null;
            held.SetReferenceCounted(false);
            held.Acquire();
            return new MulticastHold(held);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class MulticastHold(WifiManager.MulticastLock held) : IDisposable
    {
        public void Dispose()
        {
            try { if (held.IsHeld) held.Release(); }
            catch (Exception) { }
        }
    }

    private static LanSighting.Seen? Sighting(NsdServiceInfo info, IReadOnlySet<Guid> paired)
    {
        var raw = new List<KeyValuePair<string, byte[]?>>();
        if (info.Attributes is { } attributes)
            foreach (var entry in attributes) raw.Add(new KeyValuePair<string, byte[]?>(entry.Key, entry.Value));

        var addresses = new List<IPAddress>();
        if (OperatingSystem.IsAndroidVersionAtLeast(34) && info.HostAddresses is { } many)
        {
            foreach (var a in many)
                if (a?.HostAddress is { } text && IPAddress.TryParse(text, out var parsed)) addresses.Add(parsed);
        }
#pragma warning disable CA1422   // Host is the one address a resolve gives before Android 14; from 14 HostAddresses (above) is read
        else if (info.Host?.HostAddress is { } one && IPAddress.TryParse(one, out var single))
        {
            addresses.Add(single);
        }
#pragma warning restore CA1422
        return LanSighting.Read(LanSighting.Attributes(raw), addresses, info.Port, paired);
    }

    /// <summary>One service resolved: its address, port and TXT. Null when it did not resolve in time.</summary>
    private static async Task<NsdServiceInfo?> ResolveAsync(Context context, NsdManager nsd, NsdServiceInfo service, CancellationToken ct)
    {
        var done = new TaskCompletionSource<NsdServiceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResolveTimeout);
        using var _ = timeout.Token.Register(() => done.TrySetResult(null));

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            // From Android 14 the resolve is a callback that stays registered: take the first update that has an address, then drop it.
            var callback = new InfoCallback(done);
            try
            {
                nsd.RegisterServiceInfoCallback(service, context.MainExecutor!, callback);
                return await done.Task;
            }
            catch (Exception) { return null; }
            finally
            {
                try { nsd.UnregisterServiceInfoCallback(callback); }
                catch (Exception) { }
            }
        }

        try
        {
            nsd.ResolveService(service, new Resolver(done));
            return await done.Task;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class Listener(ChannelWriter<NsdServiceInfo> found, ILogger logger) : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        public void OnDiscoveryStarted(string? serviceType) { }
        public void OnDiscoveryStopped(string? serviceType) { }
        public void OnServiceFound(NsdServiceInfo? serviceInfo) { if (serviceInfo is not null) found.TryWrite(serviceInfo); }
        public void OnServiceLost(NsdServiceInfo? serviceInfo) { }

        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode)
        {
            logger.LogDebug("NSD could not start a look: {Code}", errorCode);
            found.TryComplete();
        }

        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode) { }
    }

    private sealed class Resolver(TaskCompletionSource<NsdServiceInfo?> done) : Java.Lang.Object, NsdManager.IResolveListener
    {
        public void OnServiceResolved(NsdServiceInfo? serviceInfo) => done.TrySetResult(serviceInfo);
        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) => done.TrySetResult(null);
        public void OnResolutionStopped(NsdServiceInfo? serviceInfo) { }
        public void OnStopResolutionFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) { }
    }

    private sealed class InfoCallback(TaskCompletionSource<NsdServiceInfo?> done) : Java.Lang.Object, NsdManager.IServiceInfoCallback
    {
        public void OnServiceUpdated(NsdServiceInfo serviceInfo)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34) && serviceInfo.HostAddresses is { Count: > 0 }) done.TrySetResult(serviceInfo);
        }

        public void OnServiceInfoCallbackRegistrationFailed(NsdFailure errorCode) => done.TrySetResult(null);
        public void OnServiceLost() => done.TrySetResult(null);
        public void OnServiceInfoCallbackUnregistered() { }
    }
}
