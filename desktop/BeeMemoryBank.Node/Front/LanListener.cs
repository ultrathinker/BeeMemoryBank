using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;
using BeeMemoryBank.Infrastructure.Network;

namespace BeeMemoryBank.Node;

/// <summary>Where the LAN listener binds, decided from the surfaces that are on (an interface so the tests can bind loopback).</summary>
public interface ILanBindPlanner
{
    /// <summary>
    /// The addresses to listen on and, of those, the ones the sync surface answers on. <paramref name="web"/>: the web surface is on (it listens on
    /// every interface, as "Devices on my network" always did). <paramref name="join"/>: the join door is open (it listens on every usable network, a
    /// trusted one or not: decision D2). <paramref name="sync"/>: LAN sync is on (it listens on the trusted networks only).
    /// </summary>
    LanBindPlan Plan(bool sync, bool join, bool web);

    /// <summary>What the card shows about this computer's networks (empty where the planner does not look at networks).</summary>
    IReadOnlyList<LanNetworkView> Networks { get; }
}

/// <param name="Bind">The local addresses to listen on (0.0.0.0 for every interface).</param>
/// <param name="SyncAddresses">The local addresses on which the sync surface answers: a connection to any other address of this computer gets none of it.</param>
public sealed record LanBindPlan(IReadOnlyList<IPAddress> Bind, IReadOnlyList<IPAddress> SyncAddresses)
{
    public static readonly LanBindPlan Nothing = new([], []);
}

/// <summary>The real planner: this computer's networks and whether each is trusted (<see cref="ITrustedNetworks"/>).</summary>
public sealed class TrustedNetworkBindPlanner(ITrustedNetworks trusted) : ILanBindPlanner
{
    private volatile IReadOnlyList<LanNetworkView> _networks = [];

    public IReadOnlyList<LanNetworkView> Networks => _networks;

    public LanBindPlan Plan(bool sync, bool join, bool web)
    {
        var views = trusted.Evaluate();
        _networks = views;
        var syncAddresses = sync
            ? views.Where(v => v.Trusted && v.Interface.Kind != LanInterfaceKind.Virtual).Select(v => v.Interface.Address).Distinct().ToList()
            : new List<IPAddress>();
        if (web) return new LanBindPlan([IPAddress.Any], syncAddresses);
        var bind = new List<IPAddress>(syncAddresses);
        // The join door opens on every usable network, trusted or not, a VPN's included (decision D2: an explicit action, guarded by its code); a
        // virtual adapter's never (a guest VM).
        if (join)
            bind.AddRange(views.Where(v => v.Interface.Kind is LanInterfaceKind.Lan or LanInterfaceKind.Vpn).Select(v => v.Interface.Address));
        return new LanBindPlan(bind.Distinct().ToList(), syncAddresses);
    }
}

/// <summary>A planner that always binds the same addresses (tests, and a node told where to listen): the sync surface answers on every one of them.</summary>
public sealed class FixedBindPlanner(params IPAddress[] addresses) : ILanBindPlanner
{
    public IReadOnlyList<LanNetworkView> Networks => [];

    public LanBindPlan Plan(bool sync, bool join, bool web) =>
        sync || join || web ? new LanBindPlan(addresses, sync ? addresses : []) : LanBindPlan.Nothing;
}

/// <summary>
/// The node's ONE listener on the LAN port (decision D6, docs/design/lan-peer-sync.md section 5), with three surfaces:
/// <list type="bullet">
/// <item><b>sync</b> (<see cref="LanSurfaces.Sync"/>): what a paired node needs to sync with this one directly. On while LAN sync is on, and only on the
///   addresses of trusted networks (section 6): a connection to another address of this computer does not reach it.</item>
/// <item><b>join</b>: the door of "Connect a device" (<see cref="LanJoinListener"/>, its routes and its one-time token), while a session is open.</item>
/// <item><b>web</b>: the whole front ("Devices on my network"), while that setting is on.</item>
/// </list>
/// One port, so an address a peer already stores (the join code's <c>https://192.168.1.220:5311</c>) becomes a permanent sync address as soon as this
/// node runs the new build. Each local address has its own Kestrel server, so a network that comes or goes is added or removed without touching the
/// connections on the others; with the web surface on, one server listens on every interface, as "Devices on my network" always did.
///
/// <para>Before a caller has a token it reaches only the handshake, rate limited per address (<see cref="HandshakeLimit"/>), with small bodies; a
/// connection rate per address bounds the TLS handshakes, and the server limits connections, header time and data rates. Every forwarded request loses
/// the node's identity headers on the way, and <c>/node/*</c> is never served here (it is loopback-only on the loopback front).</para>
/// </summary>
public sealed class LanListener : IAsyncDisposable
{
    /// <summary>Handshake requests (challenge, authenticate, identity, sentinel) per address per minute: what one peer needs with push-on-save, and no more.</summary>
    public const int HandshakeLimit = 60;

    /// <summary>New connections per address per minute.</summary>
    public const int ConnectionLimit = 60;

    /// <summary>Connections the sync and join servers take at once.</summary>
    public const int MaxConnections = 32;

    private readonly string _apiUrl;
    private readonly Func<string?> _webUrl;
    private readonly Func<X509Certificate2?> _certificate;
    private readonly ILanBindPlanner _planner;
    private readonly Action<LanListenerSnapshot>? _stateChanged;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<IPAddress, Server> _servers = new();
    private readonly PerAddressLimiter _handshakes = new(HandshakeLimit, TimeSpan.FromMinutes(1));
    private readonly PerAddressLimiter _connections = new(ConnectionLimit, TimeSpan.FromMinutes(1));
    private readonly HttpMessageInvoker _forwardClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    private volatile HashSet<IPAddress> _syncAddresses = new();
    private volatile LanJoinListener? _join;
    private bool _sync;
    private bool _web;
    private int _port;
    private bool _disposed;
    private Timer? _poll;
    private Timer? _debounce;
    private string? _lastBindError;

    /// <param name="apiUrl">The Api child's loopback address, where the sync and join routes are forwarded.</param>
    /// <param name="webUrl">The Web child's loopback address, for the web surface; null where there is none (the door of a test).</param>
    /// <param name="certificate">The node's TLS leaf, asked on every handshake (so a re-issued leaf needs no restart).</param>
    /// <param name="planner">Where to listen.</param>
    /// <param name="port">The LAN port (0: one the system picks, for tests).</param>
    /// <param name="stateChanged">Told what is served after every change (on a real node it writes <see cref="LanListenerState"/>).</param>
    public LanListener(string apiUrl, Func<string?> webUrl, Func<X509Certificate2?> certificate, ILanBindPlanner planner, int port,
        Action<LanListenerSnapshot>? stateChanged = null, Action<string>? log = null)
    {
        _apiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
        _webUrl = webUrl ?? (() => null);
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _port = port;
        _stateChanged = stateChanged;
        _log = log ?? (_ => { });
    }

    /// <summary>The LAN port this listener uses (the bound one once a server with port 0 listens).</summary>
    public int Port => Volatile.Read(ref _port);

    public bool SyncOn => Volatile.Read(ref _sync);
    public bool WebOn { get { lock (_servers) return _web && _servers.Values.Any(v => v.Web); } }
    public bool JoinOn => _join is not null;

    /// <summary>The addresses something listens on now.</summary>
    public IReadOnlyList<IPAddress> Bound { get { lock (_servers) return _servers.Keys.ToList(); } }

    /// <summary>The addresses the sync surface answers on now.</summary>
    public IReadOnlyList<IPAddress> SyncAddresses => _syncAddresses.ToList();

    /// <summary>This computer's networks as the planner last saw them.</summary>
    public IReadOnlyList<LanNetworkView> Networks => _planner.Networks;

    /// <summary>Why the last bind failed (another program has the port), or null.</summary>
    public string? LastBindError => Volatile.Read(ref _lastBindError);

    /// <summary>What is served now.</summary>
    public LanListenerSnapshot Snapshot()
    {
        List<IPAddress> bound;
        lock (_servers) bound = _servers.Keys.ToList();
        var sync = _syncAddresses.Where(a => bound.Contains(a) || bound.Contains(IPAddress.Any)).Select(a => a.ToString()).ToList();
        var listening = bound.Count > 0;
        return listening
            ? new LanListenerSnapshot(true, Port, Sync: sync.Count > 0, Web: WebOn, Join: _join is not null, SyncAddresses: sync)
            : LanListenerSnapshot.Closed with { Port = Port };
    }

    /// <summary>Starts watching the networks (a change rebinds within seconds) and applies the surfaces asked for so far.</summary>
    public async Task StartAsync()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        // The networks change without an event too (a category changed in Windows Settings, the router's address seen at last): looked at again regularly.
        _poll = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        await RefreshAsync();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Debounce();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Debounce();

    private void Debounce()
    {
        // A Wi-Fi join fires several changes within a second: one look, two seconds after the last of them.
        var timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _debounce, timer)?.Dispose();
    }

    /// <summary>LAN sync on or off. Rebinds at once.</summary>
    public async Task SetSyncAsync(bool on)
    {
        Volatile.Write(ref _sync, on);
        await RefreshAsync();
    }

    /// <summary>
    /// The web surface on or off. Throws <see cref="InvalidOperationException"/> when the node has no certificate, and the server's own
    /// <see cref="IOException"/> when the port is taken (the surface is then off again).
    /// </summary>
    public async Task SetWebAsync(bool on)
    {
        if (on && _certificate() is null)
            throw new InvalidOperationException("This node has no TLS certificate to serve the network with.");
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _web = on;
            try { await ReconcileCoreAsync(throwOnBindFailure: on); }
            catch
            {
                _web = false;
                await ReconcileCoreAsync(throwOnBindFailure: false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Opens the join surface for <paramref name="door"/>'s session (null closes it). Throws the server's <see cref="IOException"/> when the port is taken.</summary>
    internal async Task SetJoinAsync(LanJoinListener? door)
    {
        await _gate.WaitAsync();
        try
        {
            if (door is not null) ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disposed)
            {
                _join = null;   // closing a door after the listener stopped: nothing is bound again
                return;
            }
            _join = door;
            try { await ReconcileCoreAsync(throwOnBindFailure: door is not null); }
            catch
            {
                _join = null;
                await ReconcileCoreAsync(throwOnBindFailure: false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Moves every surface to another port (the setting changed). A port that is taken leaves nothing listening and says so.</summary>
    public async Task SetPortAsync(int port)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                Volatile.Write(ref _port, port);
                return;
            }
            if (port == Volatile.Read(ref _port)) return;
            await StopAllCoreAsync();
            Volatile.Write(ref _port, port);
            await ReconcileCoreAsync(throwOnBindFailure: false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Looks at the networks again and binds what they call for. Never throws: a failure is logged and shown on the card.</summary>
    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(30))) return;
        try
        {
            if (_disposed) return;
            await ReconcileCoreAsync(throwOnBindFailure: false);
        }
        catch (Exception ex)
        {
            _log($"[Node] LAN listener: could not apply the networks ({ex.GetType().Name}: {ex.Message}).");
        }
        finally { _gate.Release(); }
    }

    /// <summary>The caller holds <see cref="_gate"/>.</summary>
    private async Task ReconcileCoreAsync(bool throwOnBindFailure)
    {
        var plan = _planner.Plan(Volatile.Read(ref _sync) && _certificate() is not null, _join is not null, _web);
        // A door asked for with no network to open it on: no code is handed out for a port nothing serves.
        if (throwOnBindFailure && _join is not null && plan.Bind.Count == 0)
            throw new IOException("This computer is on no network the join door can open on.");
        _syncAddresses = plan.SyncAddresses.Select(Normalize).ToHashSet();
        var want = plan.Bind.Select(Normalize).ToHashSet();

        List<(IPAddress Address, Server Server)> stale;
        // A server built with the other web setting has the other limits: it is built again.
        lock (_servers) stale = _servers.Where(s => !want.Contains(s.Key) || s.Value.Web != _web).Select(s => (s.Key, s.Value)).ToList();
        foreach (var (address, server) in stale)
        {
            lock (_servers) _servers.Remove(address);
            await server.StopAsync();
        }

        Exception? failure = null;
        foreach (var address in want)
        {
            bool running;
            lock (_servers) running = _servers.ContainsKey(address);
            if (running) continue;
            try
            {
                var server = await StartServerAsync(address);
                lock (_servers) _servers[address] = server;
                Volatile.Write(ref _lastBindError, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                var why = $"port {Port} on {address} could not be opened ({ex.Message})";
                if (!string.Equals(Volatile.Read(ref _lastBindError), why, StringComparison.Ordinal))
                    _log($"[Node] LAN listener: {why}.");
                Volatile.Write(ref _lastBindError, why);
                failure ??= ex;
            }
        }
        Notify();
        if (failure is not null && throwOnBindFailure) throw failure;
    }

    private async Task StopAllCoreAsync()
    {
        List<Server> all;
        lock (_servers)
        {
            all = _servers.Values.ToList();
            _servers.Clear();
        }
        foreach (var server in all) await server.StopAsync();
        Notify();
    }

    private void Notify()
    {
        try { _stateChanged?.Invoke(Snapshot()); }
        catch { /* the state file is best effort; the listener itself must not fail for it */ }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private async Task<Server> StartServerAsync(IPAddress address)
    {
        var web = _web;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            // The default for a request; the gate below raises it per request where a route is allowed more (a push with a token, an upload on the web surface).
            options.Limits.MaxRequestBodySize = LanSurfaces.PreAuthBodyLimit;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            if (!web)
            {
                options.Limits.MaxConcurrentConnections = MaxConnections;
                options.Limits.MaxConcurrentUpgradedConnections = 0;
            }
            options.Listen(address, Port, listen =>
            {
                // Before TLS: a caller that opens connection after connection costs a handshake each, so their rate is bounded per address.
                listen.Use(next => async connection =>
                {
                    var remote = (connection.RemoteEndPoint as IPEndPoint)?.Address;
                    if (remote is not null && !_connections.TryAcquire(Normalize(remote)))
                    {
                        connection.Abort();
                        return;
                    }
                    await next(connection);
                });
                listen.UseHttps(https =>
                {
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                    https.ServerCertificateSelector = (_, _) => _certificate();
                });
            });
        });
        builder.Services.AddHttpForwarder();

        var app = builder.Build();
        var transformer = app.Services.GetRequiredService<ITransformBuilder>().Create(context =>
        {
            // Same rule as the loopback front: nothing from outside may claim to be the node, or carry the door's token further.
            foreach (var header in NodeFront.StrippedInboundIdentityHeaders)
                context.AddRequestHeaderRemove(header);
            context.AddRequestHeaderRemove(LanJoinListener.TokenHeader);
        });
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(35) };

        app.Run(ctx => DispatchAsync(ctx, forwarder, transformer, requestConfig));

        try { await app.StartAsync(); }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        // Port 0 (tests): the port the system picked becomes this listener's port, so the next server binds the same one.
        if (Port == 0
            && app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() is { } url
            && Uri.TryCreate(url.Replace("://+", "://localhost").Replace("://*", "://localhost"), UriKind.Absolute, out var uri))
            Volatile.Write(ref _port, uri.Port);

        var server = new Server(app, web);
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            // A server that went down by itself is no longer listening: forget it, so the next look binds it again and the state says so meanwhile.
            bool removed;
            lock (_servers) removed = _servers.TryGetValue(address, out var current) && ReferenceEquals(current, server) && _servers.Remove(address);
            if (removed) Notify();
        });
        return server;
    }

    private async Task DispatchAsync(HttpContext ctx, IHttpForwarder forwarder, HttpTransformer transformer, ForwarderRequestConfig config)
    {
        var path = ctx.Request.Path;
        var method = ctx.Request.Method;
        var local = ctx.Connection.LocalIpAddress is { } l ? Normalize(l) : null;

        // Loopback-only on the loopback front, and never here: the shell's and the page's controls are for this computer.
        if (path.StartsWithSegments("/node", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var hasToken = ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal);

        // 1. The sync surface, on the addresses of trusted networks.
        if (Volatile.Read(ref _sync) && local is not null && _syncAddresses.Contains(local) && LanSurfaces.IsSyncRoute(method, path))
        {
            if (LanSurfaces.IsHandshake(method, path))
            {
                var remote = ctx.Connection.RemoteIpAddress is { } r ? Normalize(r) : IPAddress.None;
                if (!_handshakes.TryAcquire(remote))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
                SetBodyLimit(ctx, LanSurfaces.PreAuthBodyLimit);
            }
            else
            {
                SetBodyLimit(ctx, !hasToken ? LanSurfaces.PreAuthBodyLimit
                    : LanSurfaces.IsLargeBody(method, path) ? LanSurfaces.LargeBodyLimit : LanSurfaces.TokenBodyLimit);
            }
            await ForwardAsync(ctx, forwarder, _apiUrl, transformer, config);
            return;
        }

        // 2. The join door, while a session of "Connect a device" is open. Its handshake routes have the sync surface's limit per address too.
        if (_join is { } door && door.Serves(method, path))
        {
            if (LanSurfaces.IsHandshake(method, path)
                && !_handshakes.TryAcquire(ctx.Connection.RemoteIpAddress is { } from ? Normalize(from) : IPAddress.None))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            SetBodyLimit(ctx, 1024 * 1024);
            await door.GateAsync(ctx, () => ForwardAsync(ctx, forwarder, _apiUrl, transformer, config));
            return;
        }

        // 3. The whole front, while "Devices on my network" is on: routed as the loopback front routes, by which process owns the path.
        if (_web && _webUrl() is { } webUrl)
        {
            SetBodyLimit(ctx, 500L * 1024 * 1024);
            var toApi = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
            await ForwardAsync(ctx, forwarder, toApi ? _apiUrl : webUrl, transformer, config);
            return;
        }

        // Anything else is not here: the same 404 an unpublished path gets from the Api.
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static void SetBodyLimit(HttpContext ctx, long limit)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;
    }

    private async Task ForwardAsync(HttpContext ctx, IHttpForwarder forwarder, string destination, HttpTransformer transformer, ForwarderRequestConfig config)
    {
        var error = await forwarder.SendAsync(ctx, destination, _forwardClient, config, transformer);
        if (error != ForwarderError.None && !ctx.Response.HasStarted)
            ctx.Response.StatusCode = error == ForwarderError.RequestBodyClient ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway;
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _poll?.Dispose();
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            _join = null;
            await StopAllCoreAsync();
        }
        finally { _gate.Release(); }
        _forwardClient.Dispose();
    }

    private sealed class Server(WebApplication app, bool web)
    {
        public bool Web { get; } = web;

        public async Task StopAsync()
        {
            // Graceful, bounded: a snapshot still streaming to a joining device is let finish, a stuck connection cannot hold a rebind.
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await app.StopAsync(stopCts.Token); } catch (OperationCanceledException) { }
            try { await app.DisposeAsync(); } catch { /* already down */ }
        }
    }

    /// <summary>A small per-address sliding count: at most N in a window, the table bounded so a scan of many addresses cannot grow it.</summary>
    internal sealed class PerAddressLimiter(int limit, TimeSpan window, int maxAddresses = 4096)
    {
        private readonly ConcurrentDictionary<IPAddress, Queue<DateTime>> _hits = new();

        public bool TryAcquire(IPAddress address)
        {
            var now = DateTime.UtcNow;
            if (_hits.Count > maxAddresses)
                foreach (var (key, q) in _hits)
                    lock (q) if (q.Count == 0 || now - q.Peek() > window) _hits.TryRemove(key, out _);
            var queue = _hits.GetOrAdd(address, _ => new Queue<DateTime>());
            lock (queue)
            {
                while (queue.Count > 0 && now - queue.Peek() > window) queue.Dequeue();
                if (queue.Count >= limit) return false;
                queue.Enqueue(now);
                return true;
            }
        }
    }
}
