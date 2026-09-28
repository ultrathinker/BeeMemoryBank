using System;
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
/// The LAN HTTPS listener a phone joins through when there is no hub (plan section 10), switched on
/// from the Connect page while the node runs. It is a second Kestrel server inside the Node process,
/// next to the loopback front; the Api and Web children are not touched, so the vault stays unlocked
/// — the old way, <c>BMB_HTTPS_ENABLED=1</c>, needed a restart and a restarted node is locked, so
/// <c>/api/join</c> answered 409.
///
/// <para>What it exposes is deliberately narrow: the four calls a join makes (<c>/api/join</c>, the
/// challenge, authenticate, and the for-join snapshot), nothing else — no web UI, no MCP. And
/// <c>/api/join</c> needs the session's one-time token from the join code in
/// <see cref="TokenHeader"/>, so a device on the LAN that has not seen this screen cannot even try a
/// password. The token is held by one attempt at a time and spent by the first join that succeeds.</para>
///
/// <para>It turns itself off after <see cref="Lifetime"/>, or as soon as the device that joined has
/// downloaded its snapshot — the last step of a join.</para>
/// </summary>
public sealed class LanJoinListener : IAsyncDisposable
{
    /// <summary>How long a session stays open if nobody joins.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Request header carrying the join code's token on <c>POST /api/join</c>.</summary>
    public const string TokenHeader = BeeMemoryBank.Core.Models.JoinCode.TokenHeader;

    private const string JoinPath = "/api/join";
    private const string SnapshotPath = "/api/sync/snapshot/for-join";

    // Everything a joining device calls, and nothing more.
    private static readonly (string Method, string Path)[] ExposedRoutes =
    [
        ("POST", JoinPath),
        ("POST", "/api/sync/challenge"),
        ("POST", "/api/sync/authenticate"),
        ("GET", SnapshotPath),
    ];

    private enum TokenState { Free, InUse, Spent }

    private readonly string _apiUrl;
    private readonly Func<X509Certificate2?> _certificate;
    private readonly IPEndPoint _endpoint;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebApplication? _app;
    private ITimer? _expiry;
    private LanJoinSession? _session;
    private byte[] _tokenBytes = [];
    private int _tokenState;

    /// <param name="apiUrl">The Api child's loopback URL the joins are forwarded to.</param>
    /// <param name="certificate">The node's TLS leaf (the one the Connect page pins).</param>
    /// <param name="endpoint">Where to listen: <c>0.0.0.0:5311</c> on a real node.</param>
    public LanJoinListener(string apiUrl, Func<X509Certificate2?> certificate, IPEndPoint endpoint, TimeProvider time)
    {
        _apiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The open session, or null when the listener is off.</summary>
    public LanJoinSession? Current => Volatile.Read(ref _session);

    /// <summary>
    /// Opens the listener with a fresh token, or returns the session already open. Throws
    /// <see cref="InvalidOperationException"/> when the node has no TLS certificate to serve.
    /// </summary>
    public async Task<LanJoinSession> EnableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_session != null) return _session;

            var probe = _certificate()
                ?? throw new InvalidOperationException("This node has no TLS certificate to serve the LAN listener with.");
            var pin = SpkiPin.Of(probe);

            var app = Build();
            try { await app.StartAsync(); }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            _tokenBytes = RandomNumberGenerator.GetBytes(BeeMemoryBank.Core.Models.JoinCode.TokenSize);
            Volatile.Write(ref _tokenState, (int)TokenState.Free);
            _app = app;
            var session = new LanJoinSession(Base64Url.EncodeToString(_tokenBytes), _time.GetUtcNow() + Lifetime, pin, _endpoint.Port);
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
            _expiry?.Dispose();
            _expiry = null;
            _session = null;
            if (_app is { } app)
            {
                _app = null;
                // Graceful: a snapshot still streaming to a device is let finish (bounded).
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await app.StopAsync(stopCts.Token); } catch (OperationCanceledException) { }
                await app.DisposeAsync();
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await DisableAsync();

    private WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Listen(_endpoint, listen => listen.UseHttps(https =>
            {
                https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13;
                https.ServerCertificateSelector = (_, _) => _certificate();
            }));
        });
        builder.Services.AddHttpForwarder();

        var app = builder.Build();
        app.Use(GateAsync);

        var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(35) };
        foreach (var (method, path) in ExposedRoutes)
        {
            app.MapForwarder(path, _apiUrl, requestConfig, transforms =>
            {
                // Same rule as the loopback front: nothing from outside may claim to be the node.
                foreach (var header in NodeFront.StrippedInboundIdentityHeaders)
                    transforms.AddRequestHeaderRemove(header);
                transforms.AddRequestHeaderRemove(TokenHeader);
            }).WithMetadata(new HttpMethodMetadata([method]));
        }
        // Anything else is not here — the same 404 an unpublished path gets from the Api.
        app.MapFallback(() => Results.NotFound());
        return app;
    }

    private async Task GateAsync(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path;
        if (path.Equals(JoinPath, StringComparison.OrdinalIgnoreCase))
        {
            await ForwardJoinAsync(ctx, next);
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
            _ = Task.Run(() => CloseAsync(session));
        }
    }

    private async Task ForwardJoinAsync(HttpContext ctx, Func<Task> next)
    {
        if (!TokenMatches(ctx.Request.Headers[TokenHeader].ToString()))
        {
            await RefuseAsync(ctx, "This connection code is not valid. Open Connect a device on the computer and use the code shown there.");
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
                await RefuseAsync(ctx, "This connection code has already been used. Start Connect a device again for a new one.");
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
    }

    private bool TokenMatches(string presented)
    {
        if (string.IsNullOrEmpty(presented) || Current == null) return false;
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
