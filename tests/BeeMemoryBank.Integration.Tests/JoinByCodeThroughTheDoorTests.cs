using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Node;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Task P7: a computer that joins another one with the join code its "Connect a device" card shows. The other computer is a real Api
/// on Kestrel behind the real <see cref="LanJoinListener"/> (the temporary door, TLS with a certificate no one trusts), and the joiner is
/// a real node's <c>POST /api/init/join</c> (what the Setup page calls). Covers: success, wrong token, wrong pin, expired door,
/// wrong password, and what the person is told in each case.
/// </summary>
public class JoinByCodeThroughTheDoorTests : IAsyncLifetime
{
    private const string Password = "doorJoinPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _dataDirA = Path.Combine(Path.GetTempPath(), "bmb_doorjoin_" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _hitsOnA = new();
    private readonly FakeClock _clock = new();
    private readonly List<BmbWebApplicationFactory> _joiners = new();
    private WebApplication _nodeA = null!;
    private X509Certificate2 _cert = null!;
    private LanJoinListener _door = null!;
    private int _doorPort;
    private Guid _nodeAId;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        Directory.CreateDirectory(_dataDirA);

        // Node A: the real Api on a real socket (the door forwards to a URL), initialized and unlocked.
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseSetting("BeeMemoryBank:DataPath", _dataDirA);
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.AddBeeApiServices(_dataDirA);
        _nodeA = builder.Build();
        await _nodeA.RunBeeApiStartupTasksAsync(_dataDirA);
        _nodeA.Use(async (ctx, next) => { _hitsOnA.Enqueue(ctx.Request.Path); await next(); });
        _nodeA.UseBeeApiPipeline();
        _nodeA.MapBeeApiEndpoints();
        await _nodeA.StartAsync();

        using (var scope = _nodeA.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "NodeA", Password);
            _nodeAId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        }
        using (var http = new HttpClient { BaseAddress = new Uri(_nodeA.Urls.First(u => u.StartsWith("http://127.0.0.1"))) })
        {
            // As the Web child calls it: with the node's internal key (without it the Api answers 404 for a path it does not publish).
            http.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
            (await http.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        }

        // The door in front of it: TLS with a certificate from nobody's CA, pinned by the code.
        _cert = CreateServerCertificate();
        _doorPort = FreePort();
        _door = new LanJoinListener(_nodeA.Urls.First(u => u.StartsWith("http://127.0.0.1")), () => _cert,
            new IPEndPoint(IPAddress.Loopback, _doorPort), _clock);
    }

    public async Task DisposeAsync()
    {
        foreach (var j in _joiners) j.Dispose();
        await _door.DisposeAsync();
        await _nodeA.StopAsync();
        await _nodeA.DisposeAsync();
        _cert.Dispose();
        try { Directory.Delete(_dataDirA, recursive: true); } catch { /* best effort, like the factory */ }
    }

    [Fact]
    public async Task Join_WithTheCode_Succeeds_RecordsThePinnedKeyOfTheOtherComputer_AndTheDoorClosesAfterTheSnapshot()
    {
        var session = await _door.EnableAsync();
        var b = NewJoiner();

        var (status, body) = await JoinAsync(b, CodeFor(session));

        status.Should().Be(HttpStatusCode.OK, body);
        using var scope = b.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().NotBeNull("the joiner is initialized");
        var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_nodeAId);
        row.Should().NotBeNull();
        row!.ApiAddress.Should().Be($"https://127.0.0.1:{_doorPort}");
        row.TlsSpki.Should().Be(session.SpkiPin, "the key the code pinned is the key the joiner dials that computer by from now on");

        (await EventuallyAsync(() => _door.Current == null)).Should().BeTrue(
            "the door turns itself off once the joined device has its snapshot");
    }

    [Fact]
    public async Task WrongToken_IsRefusedByTheDoorWithItsOwnSentence_AndNothingReachesTheApiOrIsCreated()
    {
        var session = await _door.EnableAsync();
        var b = NewJoiner();

        var (status, body) = await JoinAsync(b, new JoinCode($"https://127.0.0.1:{_doorPort}", RandomToken(), session.SpkiPin).ToString());

        status.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("not valid", "the door's sentence is shown to the person");
        _hitsOnA.Should().NotContain("/api/join", "without the one-time token the door refuses before any password reaches the node");
        using var scope = b.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task WrongPin_NeverSendsThePassword_AndTheRealCodeStillWorks()
    {
        var session = await _door.EnableAsync();
        var otherKey = SpkiPin.Of(CreateServerCertificate());
        var b = NewJoiner();

        var (status, body) = await JoinAsync(b, new JoinCode($"https://127.0.0.1:{_doorPort}", session.Token, otherKey).ToString());

        status.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("not the one the join code belongs to").And.NotContain(Password);
        _hitsOnA.Should().NotContain("/api/join", "the TLS handshake failed on the pin, so not one request byte was sent");

        // The code was not used up by the attempt: the right pin joins with the same token.
        var (okStatus, okBody) = await JoinAsync(b, CodeFor(session));
        okStatus.Should().Be(HttpStatusCode.OK, okBody);
    }

    [Fact]
    public async Task ExpiredDoor_SaysTheOtherComputerDidNotAnswer_AndNamesTheFifteenMinutes()
    {
        var session = await _door.EnableAsync();
        _clock.Advance(LanJoinListener.Lifetime + TimeSpan.FromSeconds(1));
        (await EventuallyAsync(() => _door.Current == null)).Should().BeTrue("the door closes by itself when its time is up");
        var b = NewJoiner();

        var (status, body) = await JoinAsync(b, CodeFor(session));

        status.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("did not answer").And.Contain("15 minutes");
        _hitsOnA.Should().NotContain("/api/join");
    }

    [Fact]
    public async Task WrongPassword_ShowsTheNodesOwnSentence_AndDoesNotBurnTheCode()
    {
        var session = await _door.EnableAsync();
        var b = NewJoiner();

        var (status, body) = await JoinAsync(b, CodeFor(session), password: "doorJoinPassword124");

        status.Should().Be(HttpStatusCode.BadGateway);
        body.Should().NotContain("doorJoinPassword124");

        var (okStatus, okBody) = await JoinAsync(b, CodeFor(session));
        okStatus.Should().Be(HttpStatusCode.OK, "a mistyped password must not use up the one-time code: " + okBody);
    }

    [Fact]
    public async Task ACodeThatIsNotACode_IsRefusedBeforeAnyRequestIsMade()
    {
        var b = NewJoiner();

        var (status, body) = await JoinAsync(b, "bmb-join:?a=http%3A%2F%2F192.0.2.1&t=x&s=y");

        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("not valid");
        _hitsOnA.Where(p => p != "/api/session/unlock").Should().BeEmpty("the setup's own unlock aside, nothing reached the other node");
    }

    [Fact]
    public async Task TheTypedAddress_OfAComputerWithItsOwnCa_ExplainsThatTheCodeIsTheWayIn()
    {
        await _door.EnableAsync();
        var b = NewJoiner();

        using var client = b.CreateClient();
        var response = await client.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin", displayName = "NodeB", remoteUrl = $"https://127.0.0.1:{_doorPort}", password = Password
        }, JsonOpts);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Contain("join code").And.Contain("does not trust");
        _hitsOnA.Should().NotContain("/api/join", "an untrusted certificate must stop the join before the password is sent");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string CodeFor(LanJoinSession session) =>
        new JoinCode($"https://127.0.0.1:{_doorPort}", session.Token, session.SpkiPin).ToString();

    private BmbWebApplicationFactory NewJoiner()
    {
        var b = new BmbWebApplicationFactory();
        _joiners.Add(b);
        return b;
    }

    private static async Task<(HttpStatusCode Status, string Body)> JoinAsync(BmbWebApplicationFactory joiner, string code, string password = Password)
    {
        // The Api's limiter for /api/join (5 per 5 minutes) is process-wide and counts a call that arrives from the door without the
        // internal key: other test classes of this run would eat this test's share. A real person has the whole bucket to themselves.
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        using var client = joiner.CreateClient();
        var response = await client.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin", displayName = "NodeB", remoteUrl = "", password, joinCode = code
        }, JsonOpts);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string RandomToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(JoinCode.TokenSize));

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

    /// <summary>A self-signed server certificate SChannel can use (a PFX round-trip), as the node's real leaf is.</summary>
    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=bmb-door-test", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A clock whose time and timers move only when the test says so (the door's 15 minutes).</summary>
    private sealed class FakeClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<FakeTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<FakeTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.DueAt <= _now).ToList();
                foreach (var t in due) _timers.Remove(t);
            }
            foreach (var t in due) t.Fire();
        }

        private sealed class FakeTimer(FakeClock owner, TimerCallback callback, object? state) : ITimer
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
}
