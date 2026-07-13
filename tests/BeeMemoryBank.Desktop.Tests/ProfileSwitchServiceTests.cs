using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Profiles;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// Covers <see cref="ProfileSwitchService"/> — the orchestrator that stops the current node,
/// clears the WebView session, starts the target node, and reverts on failure. The real
/// process lifecycle (spawn/attach/graceful-stop) is already covered by
/// <see cref="NodeLifecycleServiceTests"/> + BeeMemoryBank.Node.Tests; here we substitute a
/// fake <see cref="INodeLifecycleService"/> so the orchestration logic (guard, stop→start
/// ordering, revert-to-A, lastUsed invariant, cookie hygiene) is exercised deterministically
/// and fast, as the brief explicitly permits ("сфабрикованный сбойный NodeLifecycleService").
/// </summary>
public class ProfileSwitchServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ── Fixtures ────────────────────────────────────────────────────────────────

    /// <summary>Creates a real ProfileService over a temp registry with two profiles, A and B.</summary>
    private (ProfileService svc, ProfileEntry a, ProfileEntry b, string dir) CreateTwoProfiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-pswitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var profilesPath = Path.Combine(dir, "profiles.json");
        var svc = new ProfileService(
            profilesPath,
            defaultVaultDir: Path.Combine(dir, "vault-a"),
            vaultsParentDir: dir);

        // The constructor seeds one default profile (id "default"); rename it to "A".
        var a = svc.GetAll().Single();
        svc.RenameProfile(a.Id, "A");
        a = svc.GetById(a.Id);

        var b = svc.AddProfile("B", Path.Combine(dir, "vault-b"));
        svc.SetLastUsed(a.Id);

        return (svc, a, b, dir);
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    // ── Scenario 1: A → B → A without a shell restart, lastUsed tracks each step ──

    [Fact]
    public async Task Switch_AtoB_then_BtoA_BothSucceed_LastUsedTracksEachStep()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        // A → B
        var r1 = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        r1.Success.Should().BeTrue("switching A→B should succeed");
        r1.Profile!.Id.Should().Be(b.Id);
        r1.FrontUrl.Should().NotBeNullOrEmpty();
        svc.LastUsedProfileId.Should().Be(b.Id, "lastUsed must update to B after a successful start");
        lifecycle.StartCalls.Should().Be(1);
        lifecycle.StartOrder.Should().Equal(b.DataPath);

        // B → A
        var r2 = await switchSvc.SwitchToAsync(a.Id, currentProfileId: b.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        r2.Success.Should().BeTrue("switching back B→A should succeed");
        r2.Profile!.Id.Should().Be(a.Id);
        svc.LastUsedProfileId.Should().Be(a.Id, "lastUsed must update to A after switching back");
        lifecycle.StartCalls.Should().Be(2);
        lifecycle.StartOrder.Should().Equal(b.DataPath, a.DataPath);
    }

    // ── Scenario 2: B starts only after A finishes stopping — no artificial serialization ──

    [Fact]
    public async Task StopAndStartAreNotSerializedBeyondNecessary()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();

        // Simulate a graceful stop that takes a measurable amount of time, so we can prove the
        // engine AWAITS it fully before starting B (necessary serialization) and adds nothing
        // beyond that (no artificial delay).
        long stopCompletedTicks = 0;
        lifecycle.StopAction = async () =>
        {
            await Task.Delay(120);
            stopCompletedTicks = Stopwatch.GetTimestamp();
        };

        long startCalledTicks = 0;
        lifecycle.OnStartCalled = _ => startCalledTicks = Stopwatch.GetTimestamp();

        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var sw = Stopwatch.StartNew();
        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);
        sw.Stop();

        result.Success.Should().BeTrue();

        lifecycle.StopCalls.Should().Be(1, "the current node A must be stopped exactly once");
        lifecycle.StartCalls.Should().Be(1, "the target node B must be started exactly once");

        // Start must NOT begin before Stop completes — proving the engine does not overlap them
        // (overlap would risk a port collision that the engine, not bmbd, would be responsible for).
        startCalledTicks.Should().BeGreaterThan(0);
        stopCompletedTicks.Should().BeGreaterThan(0);
        var gapMs = (startCalledTicks - stopCompletedTicks) / (double)Stopwatch.Frequency * 1000.0;
        gapMs.Should().BeGreaterThanOrEqualTo(0, "Start must begin only after Stop has completed");
        // The only work between Stop-completion and Start is an instant cookie clear — so the gap
        // must be tiny, proving the engine introduces no Thread.Sleep/artificial serialization.
        gapMs.Should().BeLessThan(1500, "engine must not insert an artificial delay between stop and start");
    }

    // ── Scenario 3a: start B fails → revert to A succeeds → informative error ──────

    [Fact]
    public async Task StartBFails_RevertsToA_ReturnsInformativeError()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle()
            .WithResult(b.DataPath, success: false, "boom: B refuses to start")
            .WithResult(a.DataPath, success: true, "http://127.0.0.1:5001");
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeFalse("the requested A→B switch did not succeed");
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("B", "error must name the profile that failed to start");
        result.ErrorMessage.Should().ContainEquivalentOf("revert",
            "error must explain that the app fell back to the previous profile");

        // The engine attempted B first, then reverted to A.
        lifecycle.StartCalls.Should().Be(2);
        lifecycle.StartOrder.Should().Equal(b.DataPath, a.DataPath);
    }

    // ── Scenario 3b: start B fails AND revert to A also fails → error naming both ──

    [Fact]
    public async Task StartBFails_AndRevertToFails_ReturnsErrorNamingBothFailures()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle()
            .WithResult(b.DataPath, success: false, "boom-B")
            .WithResult(a.DataPath, success: false, "boom-A");
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("boom-B");
        result.ErrorMessage.Should().Contain("boom-A");

        lifecycle.StartCalls.Should().Be(2, "must attempt B then attempt to revert to A");
        lifecycle.StartOrder.Should().Equal(b.DataPath, a.DataPath);
    }

    // ── Scenario 4: lastUsed updates ONLY after a successful start ────────────────

    [Fact]
    public async Task LastUsedUpdatesOnlyAfterSuccessfulStart()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        // B fails to start; revert to A succeeds.
        var lifecycle = new FakeNodeLifecycle()
            .WithResult(b.DataPath, success: false, "nope")
            .WithResult(a.DataPath, success: true, "http://127.0.0.1:5002");
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var before = svc.LastUsedProfileId;
        before.Should().Be(a.Id);

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeFalse();
        svc.LastUsedProfileId.Should().Be(a.Id,
            "lastUsed must NOT flip to B because B never started successfully; it must stay on A");
    }

    // ── Scenario 5: guard blocks when an update is Applying — node untouched ──────

    [Fact]
    public async Task Guard_BlocksWhenUpdateApplying_DoesNotStopOrStartNode()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        await using var stub = new HttpStub("""{"currentStep":"Applying","percentageComplete":50,"statusMessage":"applying","errorMessage":null,"availableVersion":"9.9.9","blockedGates":null}""");

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id,
            activeFrontUrl: stub.BaseUrl, cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeFalse("the guard must reject the switch while an update is applying");
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().ContainEquivalentOf("update");

        // CRITICAL: the currently-running node must not have been disturbed.
        lifecycle.StopCalls.Should().Be(0, "guard must reject BEFORE stopping the current node");
        lifecycle.StartCalls.Should().Be(0, "guard must reject BEFORE starting the target node");
        cookies.Calls.Should().Be(0, "guard must reject BEFORE clearing cookies");
        svc.LastUsedProfileId.Should().Be(a.Id, "guard rejection must not touch lastUsed");
    }

    // ── Scenario 6: guard fails OPEN on a dead endpoint — switch proceeds ─────────

    [Fact]
    public async Task Guard_FailsOpenOnDeadEndpoint_ProceedsWithSwitch()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        // A guaranteed-dead loopback endpoint (ephemeral port we immediately released).
        var deadUrl = GetClosedLoopbackUrl();

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id,
            activeFrontUrl: deadUrl, cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeTrue("a dead guard endpoint must fail OPEN and let the switch proceed");
        lifecycle.StopCalls.Should().Be(1);
        lifecycle.StartCalls.Should().Be(1);
        svc.LastUsedProfileId.Should().Be(b.Id);
    }

    // ── Bonus guard test: a non-Applying state (e.g. Idle) does NOT block ──────────

    [Fact]
    public async Task Guard_NonApplyingState_DoesNotBlock()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        await using var stub = new HttpStub("""{"currentStep":"Idle","percentageComplete":0,"statusMessage":"idle","errorMessage":null,"availableVersion":null,"blockedGates":null}""");

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id,
            activeFrontUrl: stub.BaseUrl, cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeTrue("an Idle update state must not block an ordinary switch");
        lifecycle.StartCalls.Should().Be(1);
    }

    // ── Scenario 7: cookie clearer called exactly once; its failure is swallowed ──

    [Fact]
    public async Task CookieClearer_CalledExactlyOnce_AndItsExceptionDoesNotBreakSwitch()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var throwingCookies = new FakeCookieClearer(throwOnCall: new InvalidOperationException("cookie boom"));
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            throwingCookies, progress: null, CancellationToken.None);

        throwingCookies.Calls.Should().Be(1, "cookies must be cleared exactly once per switch");
        result.Success.Should().BeTrue("a cookie-clear failure must NOT abort the switch");
        svc.LastUsedProfileId.Should().Be(b.Id);
    }

    [Fact]
    public async Task CookieClearer_CalledExactlyOnce_OnSuccessfulSwitch()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        cookies.Calls.Should().Be(1, "cookies must be cleared exactly once per switch");
    }

    // ── Edge case: target profile not found → Error, nothing touched ─────────────

    [Fact]
    public async Task UnknownTargetProfile_ReturnsErrorWithoutTouchingNode()
    {
        var (svc, a, _, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var result = await switchSvc.SwitchToAsync("does-not-exist", currentProfileId: a.Id,
            activeFrontUrl: null, cookies, progress: null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        lifecycle.StopCalls.Should().Be(0, "must not stop anything for an unknown target");
        lifecycle.StartCalls.Should().Be(0);
    }

    // ── Single-flight: a second concurrent switch is rejected, not queued ────────

    [Fact]
    public async Task ConcurrentSwitches_SecondCallIsRejected_FirstStillSucceeds()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        // Hold the first call inside StopAsync long enough for the second call to observe
        // the gate as held.
        lifecycle.StopAction = async () => await Task.Delay(300);
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        var task1 = switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);
        // Started without awaiting task1 - by now task1 has synchronously acquired the gate
        // and is inside the delayed StopAsync.
        var task2 = switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);

        results.Count(r => r.Success).Should().Be(1, "exactly one of the two concurrent switches must succeed");
        results.Count(r => !r.Success && r.ErrorMessage!.Contains("already in progress")).Should().Be(1,
            "the other must be rejected as already-in-progress, not silently queued or corrupted");
    }

    // ── Cancellation: an already-cancelled token refuses the switch before touching anything ──

    [Fact]
    public async Task AlreadyCancelledToken_RefusesSwitch_WithoutStoppingOrStartingNode()
    {
        var (svc, a, b, _) = CreateTwoProfiles();
        var lifecycle = new FakeNodeLifecycle();
        var cookies = new FakeCookieClearer();
        var switchSvc = new ProfileSwitchService(svc, lifecycle);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await switchSvc.SwitchToAsync(b.Id, currentProfileId: a.Id, activeFrontUrl: null,
            cookies, progress: null, cts.Token);

        result.Success.Should().BeFalse();
        lifecycle.StopCalls.Should().Be(0,
            "a switch cancelled before it starts must not hard-kill the perfectly healthy current node");
        lifecycle.StartCalls.Should().Be(0);
        svc.LastUsedProfileId.Should().Be(a.Id);
    }

    /// <summary>Grabs an ephemeral loopback port, releases it, and returns a URL that is
    /// therefore (almost certainly) closed — for fail-open guard tests.</summary>
    private static string GetClosedLoopbackUrl()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Controllable <see cref="INodeLifecycleService"/>: counts calls, records the order of
    /// Start data dirs, and returns configurable per-dataPath results (default: success).
    /// </summary>
    private sealed class FakeNodeLifecycle : INodeLifecycleService
    {
        private readonly Dictionary<string, NodeLifecycleResult> _resultsByDataPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly NodeLifecycleResult _defaultSuccess;

        public int StopCalls;
        public int StartCalls;
        public List<string> StartOrder = new();

        public Func<Task>? StopAction;
        public Action<string>? OnStartCalled;

        public FakeNodeLifecycle(string defaultFrontUrl = "http://127.0.0.1:59999")
        {
            _defaultSuccess = NodeLifecycleResult.Ok(defaultFrontUrl);
        }

        public FakeNodeLifecycle WithResult(string dataPath, bool success, string frontUrlOrError)
        {
            _resultsByDataPath[Normalize(dataPath)] = success
                ? NodeLifecycleResult.Ok(frontUrlOrError)
                : NodeLifecycleResult.Error(frontUrlOrError);
            return this;
        }

        public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct)
        {
            Interlocked.Increment(ref StopCalls);
            if (StopAction != null)
            {
                await StopAction();
            }
        }

        public Task<NodeLifecycleResult> StartOrAttachAsync(string dataDir, IProgress<string>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref StartCalls);
            StartOrder.Add(dataDir);
            OnStartCalled?.Invoke(dataDir);
            var key = Normalize(dataDir);
            var result = _resultsByDataPath.TryGetValue(key, out var r) ? r : _defaultSuccess;
            return Task.FromResult(result);
        }

        private static string Normalize(string p) =>
            Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Counts ClearAllCookiesAsync calls and can optionally throw to test resilience.</summary>
    private sealed class FakeCookieClearer : IWebViewCookieClearer
    {
        public int Calls;
        private readonly Exception? _throwOnCall;

        public FakeCookieClearer(Exception? throwOnCall = null)
        {
            _throwOnCall = throwOnCall;
        }

        public Task ClearAllCookiesAsync()
        {
            Interlocked.Increment(ref Calls);
            if (_throwOnCall != null)
            {
                throw _throwOnCall;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on a free loopback port for the update-guard tests. Returns a
    /// fixed JSON body (and 200) for every GET. Avoids HttpListener URL-ACL permission issues.
    /// </summary>
    private sealed class HttpStub : IAsyncDisposable, IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _body;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;

        public string BaseUrl { get; }

        public HttpStub(string body)
        {
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _serverTask = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    // Drain the request line + headers (up to the blank line) so the client is happy.
                    var buffer = new byte[4096];
                    var acc = new StringBuilder();
                    while (!acc.ToString().Contains("\r\n\r\n") && acc.Length < 8192)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        acc.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }

                    var bodyBytes = Encoding.UTF8.GetBytes(_body);
                    var resp =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Connection: close\r\n\r\n";
                    var respBytes = Encoding.ASCII.GetBytes(resp);
                    await stream.WriteAsync(respBytes, _cts.Token).ConfigureAwait(false);
                    await stream.WriteAsync(bodyBytes, _cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Per-connection errors are irrelevant to the test.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { await Task.WhenAny(_serverTask, Task.Delay(2000)); } catch { }
            _cts.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
