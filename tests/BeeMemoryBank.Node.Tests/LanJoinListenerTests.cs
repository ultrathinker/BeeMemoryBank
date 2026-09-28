using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// The on-demand LAN listener behind "Connect a device" (plan section 10): off until enabled, only the
/// join calls reachable, <c>/api/join</c> gated by the one-time token, and off again by itself after a
/// completed join or after <see cref="LanJoinListener.Lifetime"/>. The Api behind it is a stub that
/// records what reached it.
/// </summary>
public class LanJoinListenerTests : IAsyncLifetime
{
    private const string RightPassword = "right-password";
    private const string StubBearerToken = "stub-sync-token";

    private readonly ConcurrentQueue<(string Path, string? InternalKey)> _apiHits = new();
    private readonly ManualTimeProvider _time = new();
    private WebApplication _api = null!;
    private X509Certificate2 _cert = null!;
    private LanJoinListener _listener = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _api = await StartApiStubAsync();
        _cert = TestCertificates.CreateServerCertificate();
        _port = FreePort();
        _listener = new LanJoinListener(_api.Urls.First(), () => _cert, new IPEndPoint(IPAddress.Loopback, _port), _time);
    }

    public async Task DisposeAsync()
    {
        await _listener.DisposeAsync();
        await _api.StopAsync();
        await _api.DisposeAsync();
        _cert.Dispose();
    }

    [Fact]
    public async Task Off_UntilEnabled_ThenServesWithThePinnedCertificate()
    {
        using var client = PinnedClient(SpkiPin.Of(_cert));
        await FluentActions.Awaiting(() => client.PostAsync("/api/sync/challenge", null))
            .Should().ThrowAsync<HttpRequestException>("nothing listens on the LAN until the user asks for it");

        var session = await _listener.EnableAsync();

        session.SpkiPin.Should().Be(SpkiPin.Of(_cert), "the join code must pin the key the listener actually serves");
        session.ExpiresAt.Should().Be(_time.GetUtcNow() + LanJoinListener.Lifetime);
        (await client.PostAsync("/api/sync/challenge", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OnlyTheJoinCallsAreExposed()
    {
        await _listener.EnableAsync();
        using var client = PinnedClient(SpkiPin.Of(_cert));

        (await client.GetAsync("/api/articles")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _apiHits.Select(h => h.Path).Should().NotContain(["/api/articles", "/"],
            "the listener must not become a LAN door to the rest of the Api or the web UI");
    }

    [Fact]
    public async Task Join_WithoutOrWithAWrongToken_IsRefused_AndNeverReachesTheApi()
    {
        await _listener.EnableAsync();
        using var client = PinnedClient(SpkiPin.Of(_cert));

        (await JoinAsync(client, token: null, RightPassword)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JoinAsync(client, token: Base64UrlToken(), RightPassword)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JoinAsync(client, token: "not base64!", RightPassword)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _apiHits.Should().NotContain(h => h.Path == "/api/join",
            "without the code from the screen a LAN device must not even get to try a password");
    }

    [Fact]
    public async Task Join_WithTheToken_IsForwarded_WithoutTheCallersIdentityHeaders()
    {
        var session = await _listener.EnableAsync();
        using var client = PinnedClient(session.SpkiPin);

        using var req = JoinRequest(session.Token, RightPassword);
        req.Headers.Add("X-Internal-Key", "forged");
        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);

        _apiHits.Should().ContainSingle(h => h.Path == "/api/join")
            .Which.InternalKey.Should().BeNull("a LAN caller must never be able to claim to be the node");
    }

    [Fact]
    public async Task Token_SurvivesAWrongPassword_IsSpentByTheFirstSuccessfulJoin()
    {
        var session = await _listener.EnableAsync();
        using var client = PinnedClient(session.SpkiPin);

        (await JoinAsync(client, session.Token, "typo")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await JoinAsync(client, session.Token, RightPassword)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a mistyped password must not burn the code");

        var again = await JoinAsync(client, session.Token, RightPassword);
        again.StatusCode.Should().Be(HttpStatusCode.Forbidden, "one code, one device");
        (await again.Content.ReadAsStringAsync()).Should().Contain("already been used");
    }

    [Fact]
    public async Task AfterTheJoinedDeviceDownloadedItsSnapshot_TheListenerTurnsItselfOff()
    {
        var session = await _listener.EnableAsync();
        using var client = PinnedClient(session.SpkiPin);
        (await JoinAsync(client, session.Token, RightPassword)).EnsureSuccessStatusCode();

        // The real sequence: challenge, authenticate, then the snapshot under the issued bearer token.
        (await client.PostAsync("/api/sync/challenge", null)).EnsureSuccessStatusCode();
        var token = (await (await client.PostAsync("/api/sync/authenticate", null)).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        (await SnapshotAsync(client, bearer: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _listener.Current.Should().NotBeNull("a refused snapshot is not a finished join");

        (await SnapshotAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the listener must pass the phone's bearer token through to the Api");

        (await EventuallyAsync(() => _listener.Current == null)).Should().BeTrue("a completed join ends the session");
        using var fresh = PinnedClient(session.SpkiPin);
        await FluentActions.Awaiting(() => fresh.PostAsync("/api/sync/challenge", null))
            .Should().ThrowAsync<HttpRequestException>("a finished join closes the LAN door");
    }

    [Fact]
    public async Task ASnapshotWithoutACompletedJoin_DoesNotCloseTheListener()
    {
        await _listener.EnableAsync();
        using var client = PinnedClient(SpkiPin.Of(_cert));

        (await SnapshotAsync(client, StubBearerToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(200);

        _listener.Current.Should().NotBeNull("only the device that joined through this session ends it");
    }

    [Fact]
    public async Task AfterFifteenMinutes_TheListenerTurnsItselfOff()
    {
        await _listener.EnableAsync();

        _time.Advance(LanJoinListener.Lifetime - TimeSpan.FromSeconds(1));
        _listener.Current.Should().NotBeNull();

        _time.Advance(TimeSpan.FromSeconds(1));
        (await EventuallyAsync(() => _listener.Current == null)).Should().BeTrue("an unused session closes after 15 minutes");
    }

    [Fact]
    public async Task EnableAgain_WhileOpen_KeepsTheSession_AfterDisable_IssuesANewToken()
    {
        var first = await _listener.EnableAsync();
        (await _listener.EnableAsync()).Should().Be(first, "pressing the button twice must not invalidate the code on screen");

        await _listener.DisableAsync();
        _listener.Current.Should().BeNull();
        var second = await _listener.EnableAsync();

        second.Token.Should().NotBe(first.Token);
        using var client = PinnedClient(second.SpkiPin);
        (await JoinAsync(client, first.Token, RightPassword)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a code from a closed session must not open a new one");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private HttpClient PinnedClient(string pin) =>
        new(SpkiPin.CreatePinnedHandler(pin)) { BaseAddress = new Uri($"https://127.0.0.1:{_port}") };

    private static Task<HttpResponseMessage> SnapshotAsync(HttpClient client, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot/for-join");
        if (bearer != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> JoinAsync(HttpClient client, string? token, string password) =>
        client.SendAsync(JoinRequest(token, password));

    private static HttpRequestMessage JoinRequest(string? token, string password)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/join") { Content = JsonContent.Create(new { masterPassword = password }) };
        if (token != null) req.Headers.Add(LanJoinListener.TokenHeader, token);
        return req;
    }

    private static string Base64UrlToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private async Task<WebApplication> StartApiStubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            _apiHits.Enqueue((ctx.Request.Path, ctx.Request.Headers.TryGetValue("X-Internal-Key", out var k) ? k.ToString() : null));
            await next();
        });
        app.MapPost("/api/join", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            return body.GetProperty("masterPassword").GetString() == RightPassword
                ? Results.Ok(new { joined = true })
                : Results.Json(new { error = "Invalid master password" }, statusCode: 401);
        });
        app.MapPost("/api/sync/challenge", () => Results.Ok(new { challenge = "c" }));
        app.MapPost("/api/sync/authenticate", () => Results.Ok(new { token = StubBearerToken }));
        // Like the real endpoint: only under the token authenticate issued.
        app.MapGet("/api/sync/snapshot/for-join", (HttpContext ctx) =>
            ctx.Request.Headers.Authorization.ToString() == $"Bearer {StubBearerToken}"
                ? Results.Bytes(new byte[64 * 1024], "application/gzip")
                : Results.Unauthorized());
        app.MapGet("/api/articles", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/", () => Results.Ok("web"));
        await app.StartAsync();
        return app;
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock and timers move only when the test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt <= _now).ToList();
            foreach (var t in due) _timers.Remove(t);
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                DueAt = owner._now + dueTime;
                owner._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() { lock (owner._gate) owner._timers.Remove(this); }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal static class TestCertificates
{
    /// <summary>
    /// A self-signed TLS server certificate whose key SChannel can use (a PFX round-trip, as
    /// NodeFront.CachedLeafCert does for the real leaf).
    /// </summary>
    public static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=bmb-lan-test", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
