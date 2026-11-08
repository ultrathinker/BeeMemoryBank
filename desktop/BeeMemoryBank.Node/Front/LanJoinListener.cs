using System;
using System.Linq;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Node;

/// <summary>One "Connect a device" session: what the Connect page puts into the join code.</summary>
public sealed record LanJoinSession(string Token, DateTimeOffset ExpiresAt, string SpkiPin, int Port);

/// <summary>
/// The door a phone or another computer joins through when there is no hub (plan section 10), opened from the Connect page while the node
/// runs: the JOIN surface of the node's one LAN listener (<see cref="LanListener"/>, on the LAN port beside the sync and web surfaces). The Api
/// and Web children are not touched, so the vault stays unlocked - the old way, <c>BMB_HTTPS_ENABLED=1</c>, needed a restart and a restarted
/// node is locked, so <c>/api/join</c> answered 409. The door opens on every usable network, a trusted one or not (decision D2: it is an explicit
/// action, guarded by the token); a join that succeeds makes the network it came in on a trusted one (decision D1).
///
/// <para>What it exposes is deliberately narrow: the four calls a join makes (<c>/api/join</c>, the
/// challenge, authenticate, and the for-join snapshot) and the joiner's abort of a failed join
/// (<c>/api/join/abort</c>), nothing else — no web UI, no MCP. And <c>/api/join</c> and its abort need
/// the session's one-time token from the join code in <see cref="TokenHeader"/>, so a device on the LAN
/// that has not seen this screen cannot even try a password. The token is held by one attempt at a time
/// and spent by the first join that succeeds; the abort does not change its state.</para>
///
/// <para>It turns itself off after <see cref="Lifetime"/>. As soon as the device that joined has
/// downloaded its snapshot — the last step of a join — the session is over, and only the abort stays
/// reachable for <see cref="AbortGrace"/> more: what can still fail on the device after the download (the
/// signature, the import) is the failure the abort exists for.</para>
/// </summary>
public sealed class LanJoinListener : IAsyncDisposable
{
    /// <summary>How long a session stays open if nobody joins.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Request header carrying the join code's token on <c>POST /api/join</c>.</summary>
    public const string TokenHeader = BeeMemoryBank.Core.Models.JoinCode.TokenHeader;

    /// <summary>How long after the joined device has its snapshot the door stays up for the abort of a join that failed on the device.</summary>
    public static readonly TimeSpan AbortGrace = TimeSpan.FromMinutes(2);

    private const string JoinPath = "/api/join";
    private const string AbortPath = "/api/join/abort";
    private const string SnapshotPath = "/api/sync/snapshot/for-join";

    // Everything a joining device calls, and nothing more.
    private static readonly (string Method, string Path)[] ExposedRoutes =
    [
        ("POST", JoinPath),
        ("POST", AbortPath),
        ("POST", "/api/sync/challenge"),
        ("POST", "/api/sync/authenticate"),
        ("GET", SnapshotPath),
    ];

    private enum TokenState { Free, InUse, Spent }

    private readonly LanListener _host;
    private readonly bool _ownsHost;
    private readonly Func<X509Certificate2?> _certificate;
    private readonly TimeProvider _time;
    private readonly Action<IPAddress?>? _joined;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _open;       // the join surface is up on the host (guarded by _gate)
    private ITimer? _expiry;
    private LanJoinSession? _session;
    private byte[] _tokenBytes = [];
    private int _tokenState;
    private int _finishing;   // 1 from the joined device's snapshot until the grace ends: only the abort is served
    private bool _disposed;   // guarded by _gate

    /// <summary>
    /// A door with a listener of its own on one fixed address (the tests; the node itself uses the other constructor and its one LAN listener).
    /// </summary>
    /// <param name="apiUrl">The Api child's loopback URL the joins are forwarded to.</param>
    /// <param name="certificate">The node's TLS leaf (the one the Connect page pins).</param>
    /// <param name="endpoint">Where to listen.</param>
    public LanJoinListener(string apiUrl, Func<X509Certificate2?> certificate, IPEndPoint endpoint, TimeProvider time)
        : this(new LanListener(apiUrl ?? throw new ArgumentNullException(nameof(apiUrl)), () => null,
                certificate ?? throw new ArgumentNullException(nameof(certificate)),
                new FixedBindPlanner((endpoint ?? throw new ArgumentNullException(nameof(endpoint))).Address), endpoint.Port),
            certificate, time, joined: null, ownsHost: true)
    {
    }

    /// <param name="host">The node's LAN listener: the door is its join surface.</param>
    /// <param name="certificate">The node's TLS leaf (the one the Connect page pins).</param>
    /// <param name="joined">Told the local address a join came in on, once it succeeded: the network a device was paired on is trusted (decision D1).</param>
    public LanJoinListener(LanListener host, Func<X509Certificate2?> certificate, TimeProvider time, Action<IPAddress?>? joined = null)
        : this(host, certificate, time, joined, ownsHost: false)
    {
    }

    private LanJoinListener(LanListener host, Func<X509Certificate2?> certificate, TimeProvider time, Action<IPAddress?>? joined, bool ownsHost)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _joined = joined;
        _ownsHost = ownsHost;
    }

    /// <summary>The open session, or null when the listener is off.</summary>
    public LanJoinSession? Current => Volatile.Read(ref _session);

    /// <summary>
    /// Opens the listener with a fresh token, or returns the session already open. Throws
    /// <see cref="InvalidOperationException"/> when the node has no TLS certificate to serve, and
    /// <see cref="ObjectDisposedException"/> (an <see cref="InvalidOperationException"/> too) once the node has
    /// disposed the listener on its way out: a late request of the Connect page must not open a network
    /// listener after the shutdown has closed it.
    /// </summary>
    public async Task<LanJoinSession> EnableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session != null) return _session;
            // The door of a finished join is still up for its abort: close it before a new session binds the same port.
            if (Volatile.Read(ref _finishing) == 1) await CloseCoreAsync();

            var probe = _certificate()
                ?? throw new InvalidOperationException("This node has no TLS certificate to serve the LAN listener with.");
            var pin = SpkiPin.Of(probe);

            // The join surface of the one LAN listener: on every usable network, trusted or not (decision D2), until the session ends.
            await _host.SetJoinAsync(this);
            _open = true;

            _tokenBytes = RandomNumberGenerator.GetBytes(BeeMemoryBank.Core.Models.JoinCode.TokenSize);
            Volatile.Write(ref _tokenState, (int)TokenState.Free);
            var session = new LanJoinSession(Base64Url.EncodeToString(_tokenBytes), _time.GetUtcNow() + Lifetime, pin, _host.Port);
            _session = session;
            _expiry = _time.CreateTimer(_ => _ = CloseAsync(session), null, Lifetime, Timeout.InfiniteTimeSpan);
            return session;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the listener. Safe to call when it is already off.</summary>
    public Task DisableAsync() => CloseAsync(null);

    /// <summary>
    /// Closes the listener — when <paramref name="only"/> is given, only if that session is still the
    /// open one, so a late timer or join of a closed session cannot end the next one.
    /// </summary>
    private async Task CloseAsync(LanJoinSession? only)
    {
        await _gate.WaitAsync();
        try
        {
            if (only != null && !ReferenceEquals(only, _session)) return;
            await CloseCoreAsync();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the listener and forgets the session; the caller holds <see cref="_gate"/>.</summary>
    private async Task CloseCoreAsync()
    {
        _expiry?.Dispose();
        _expiry = null;
        _session = null;
        Volatile.Write(ref _finishing, 0);
        if (_open)
        {
            _open = false;
            // Graceful: a snapshot still streaming to a device is let finish (bounded by the listener).
            await _host.SetJoinAsync(null);
        }
    }

    /// <summary>
    /// The joined device has its snapshot: the session ends (the code is spent, <see cref="Current"/> is null) but the listener stays
    /// up for <see cref="AbortGrace"/>, serving only the abort of a join that then fails on the device.
    /// </summary>
    private async Task FinishAsync(LanJoinSession session)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(session, _session)) return;
            _expiry?.Dispose();
            _session = null;
            _lastFinished = session;
            Volatile.Write(ref _finishing, 1);
            var finished = session;
            _expiry = _time.CreateTimer(_ => _ = CloseFinishedAsync(finished), null, AbortGrace, Timeout.InfiniteTimeSpan);
        }
        finally { _gate.Release(); }
    }

    private async Task CloseFinishedAsync(LanJoinSession finished)
    {
        await _gate.WaitAsync();
        try
        {
            // A late timer of a grace that a new session has replaced must not close that session.
            if (_session is null && _open && Volatile.Read(ref _finishing) == 1 && ReferenceEquals(finished, _lastFinished))
                await CloseCoreAsync();
        }
        finally { _gate.Release(); }
    }

    private LanJoinSession? _lastFinished;

    public async ValueTask DisposeAsync()
    {
        // Behind the gate, so an enable that is opening the listener right now finishes first and is closed
        // below, and every enable after this one is refused.
        await _gate.WaitAsync();
        try { _disposed = true; }
        finally { _gate.Release(); }

        await DisableAsync();
        if (_ownsHost) await _host.DisposeAsync();
    }

    /// <summary>True for a route of the door (the join calls, and nothing more).</summary>
    internal bool Serves(string method, PathString path) =>
        path.HasValue && ExposedRoutes.Any(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Path, path.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The door's rules for one request of its routes; <paramref name="next"/> forwards it to the Api.</summary>
    internal async Task GateAsync(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path;
        if (path.Equals(JoinPath, StringComparison.OrdinalIgnoreCase))
        {
            await ForwardJoinAsync(ctx, next);
            return;
        }
        if (path.Equals(AbortPath, StringComparison.OrdinalIgnoreCase))
        {
            await ForwardAbortAsync(ctx, next);
            return;
        }
        // The session is over (the device has its snapshot): nothing but the abort is served while the door waits out its grace.
        if (Volatile.Read(ref _finishing) == 1)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var session = Current;
        await next();

        // The device that joined has its data: the join is over, and so is the listener.
        if (path.Equals(SnapshotPath, StringComparison.OrdinalIgnoreCase)
            && ctx.Response.StatusCode == StatusCodes.Status200OK
            && Volatile.Read(ref _tokenState) == (int)TokenState.Spent
            && session != null)
        {
            _ = Task.Run(() => FinishAsync(session));
        }
    }

    private async Task ForwardJoinAsync(HttpContext ctx, Func<Task> next)
    {
        if (!TokenMatches(ctx.Request.Headers[TokenHeader].ToString()))
        {
            await RefuseAsync(ctx, "This join code is not valid. Open Connect a device on the computer and use the code shown there.");
            return;
        }

        // One attempt at a time: a wrong password frees the token for the next try, a successful join
        // spends it, and two devices racing with the same code cannot both get in.
        switch ((TokenState)Interlocked.CompareExchange(ref _tokenState, (int)TokenState.InUse, (int)TokenState.Free))
        {
            case TokenState.InUse:
                await RefuseAsync(ctx, "Another device is joining with this code right now. Try again in a moment.", StatusCodes.Status409Conflict);
                return;
            case TokenState.Spent:
                await RefuseAsync(ctx, "This join code has already been used. Start Connect a device again for a new one.");
                return;
        }

        var joined = false;
        try
        {
            await next();
            joined = ctx.Response.StatusCode == StatusCodes.Status200OK;
        }
        finally
        {
            Volatile.Write(ref _tokenState, (int)(joined ? TokenState.Spent : TokenState.Free));
        }
        if (joined)
        {
            try { _joined?.Invoke(ctx.Connection.LocalIpAddress); }
            catch { /* remembering the network is a convenience; the join itself succeeded */ }
        }
    }

    /// <summary>
    /// The abort of a failed join: it needs the code's token like the join, but changes nothing about it (the join may have spent it
    /// already), and it is served after the session ended too, during the grace. What it may do is decided by the Api: the master
    /// password, and only the never-synced row of that attempt.
    /// </summary>
    private async Task ForwardAbortAsync(HttpContext ctx, Func<Task> next)
    {
        if (!TokenMatches(ctx.Request.Headers[TokenHeader].ToString(), allowFinishing: true))
        {
            await RefuseAsync(ctx, "This join code is not valid. Open Connect a device on the computer and use the code shown there.");
            return;
        }
        await next();
    }

    private bool TokenMatches(string presented, bool allowFinishing = false)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        if (Current == null && !(allowFinishing && Volatile.Read(ref _finishing) == 1)) return false;
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(presented); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(bytes, _tokenBytes);
    }

    private static Task RefuseAsync(HttpContext ctx, string error, int status = StatusCodes.Status403Forbidden)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error });
    }
}
