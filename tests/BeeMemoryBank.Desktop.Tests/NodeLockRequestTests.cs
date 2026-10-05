using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The shell's "lock the vault now" (card BMB-116), as the sleep handler of both the Windows and the macOS shell makes it: POST
/// /node/lock with the internal key of the node the shell started. 2xx = locked, 401/403 = not authorized, 501 = an older node, a timeout or
/// a dead node = a failure with a fixed text; the key is never in a text or a log line; a node the shell did not start gets no request.
/// </summary>
public sealed class NodeLockRequestTests
{
    private const string Key = "shell-lock-test-key-0123456789abcdef";
    private const string Front = "http://127.0.0.1:5310";

    private readonly ConcurrentQueue<string> _log = new();

    private NodeLockRequest Make(HttpMessageHandler handler, string? frontUrl = Front, string? key = Key, TimeSpan? timeout = null) =>
        new(() => frontUrl, () => key, handler, timeout, _log.Enqueue);

    private sealed class Seen(string method, Uri? uri, string? key, string? role, string? userId)
    {
        public string Method { get; } = method;
        public Uri? Uri { get; } = uri;
        public string? Key { get; } = key;
        public string? Role { get; } = role;
        public string? UserId { get; } = userId;
    }

    /// <summary>A handler that records what it was asked and answers (or fails) as told.</summary>
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Seen> Requests { get; } = new();

        public Handler(HttpStatusCode status) : this(_ => Task.FromResult(new HttpResponseMessage(status))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new Seen(request.Method.Method, request.RequestUri,
                request.Headers.TryGetValues("X-Internal-Key", out var k) ? string.Join(",", k) : null,
                request.Headers.TryGetValues("X-User-Role", out var r) ? string.Join(",", r) : null,
                request.Headers.TryGetValues("X-User-Id", out var u) ? string.Join(",", u) : null));
            return await answer(cancellationToken);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.OK)]
    public async Task A2xx_IsLocked_AndTheRequestCarriesTheKey(HttpStatusCode status)
    {
        var handler = new Handler(status);

        var result = await Make(handler).RequestAsync(CancellationToken.None);

        result.Should().Be(new SleepLockResult(true));
        var seen = handler.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("POST");
        seen.Uri.Should().Be(new Uri("http://127.0.0.1:5310/node/lock"));
        seen.Key.Should().Be(Key, "the node's front answers /node/lock only to its own key");
        seen.Role.Should().BeNull("the identity the node's Api sees is the node's own, set by the front, not claimed by the shell");
        _log.Should().BeEmpty("a success has nothing to report");
    }

    [Fact]
    public async Task ATrailingSlashOnTheFrontAddress_DoesNotChangeTheRoute()
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        (await Make(handler, frontUrl: Front + "/").RequestAsync(CancellationToken.None)).Succeeded.Should().BeTrue();

        handler.Requests.Single().Uri.Should().Be(new Uri("http://127.0.0.1:5310/node/lock"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A401Or403_IsNotAuthorized_AWarningWithTheStatus(HttpStatusCode status)
    {
        var result = await Make(new Handler(status)).RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.HttpStatus.Should().Be((int)status);
        result.LogOnly.Should().BeFalse("a refused key is a real problem: the person is told");
        result.Detail.Should().Contain("not authorized").And.Contain(((int)status).ToString()).And.NotContain(Key);
        _log.Should().ContainSingle().Which.Should().Contain("not authorized");
    }

    [Fact]
    public async Task A501_IsReportedWithItsStatus_SoTheMacMonitorKeepsItQuiet()
    {
        var result = await Make(new Handler(HttpStatusCode.NotImplemented)).RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.HttpStatus.Should().Be(501);
        result.LogOnly.Should().BeFalse("the 501 special case is the monitor's, decided on the status");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AnyOtherFailureStatus_IsAFailureWithTheStatus_AsBefore(HttpStatusCode status)
    {
        var result = await Make(new Handler(status)).RequestAsync(CancellationToken.None);

        result.Should().Be(new SleepLockResult(false, $"The node answered {(int)status} to the lock request.", (int)status));
    }

    [Fact]
    public async Task ATimeout_IsAFailureAtTheBound_NotAHang()
    {
        var handler = new Handler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await Make(handler, timeout: TimeSpan.FromMilliseconds(300)).RequestAsync(CancellationToken.None);
        clock.Stop();

        result.Succeeded.Should().BeFalse();
        result.HttpStatus.Should().BeNull();
        result.Detail.Should().Contain("did not answer").And.Contain("0.3 seconds").And.NotContain(Key);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACancelledRequest_IsAFailure_NotAnException()
    {
        var handler = new Handler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await Make(handler).RequestAsync(cts.Token);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("cancelled");
    }

    [Fact]
    public async Task ADeadNode_IsAFailureWithAFixedText_EvenIfTheExceptionTextCarriesTheKey()
    {
        // a hostile or careless exception message: the result must not repeat it
        var handler = new Handler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused while sending X-Internal-Key: {Key}"));

        var result = await Make(handler).RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("The node could not be reached (ConnectionError).");
        _log.Should().OnlyContain(line => !line.Contains(Key));
    }

    [Fact]
    public async Task AnyOtherException_IsAFailureWithTheTypeOnly()
    {
        var handler = new Handler(_ => throw new InvalidOperationException($"secret {Key}"));

        var result = await Make(handler).RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("The lock request failed (InvalidOperationException).");
        _log.Should().OnlyContain(line => !line.Contains(Key));
    }

    [Fact]
    public async Task NoNodeOpen_IsNothingToLock_AndNothingIsSent()
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        var nothing = await Make(handler, frontUrl: null).RequestAsync(CancellationToken.None);
        var empty = await Make(handler, frontUrl: "").RequestAsync(CancellationToken.None);

        nothing.Should().Be(new SleepLockResult(true, "No node is open, so there is nothing to lock."));
        empty.Should().Be(nothing);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ANodeTheAppDidNotStart_HasNoKey_NothingIsSent_TheResultIsLogOnly(string? key)
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        var result = await Make(handler, key: key).RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LogOnly.Should().BeTrue("one log line, no banner");
        result.Detail.Should().Contain("did not start").And.Contain("no key");
        handler.Requests.Should().BeEmpty("a request without the key would only be refused, and a stale key must not be sent");
        _log.Should().ContainSingle();
    }

    [Theory]
    [InlineData("http://192.0.2.7:5310")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com:5311")]
    [InlineData("not a url")]
    [InlineData("file:///etc/passwd")]
    public async Task AnAddressThatIsNotOnThisComputer_GetsNoRequest_SoTheKeyNeverLeaves(string frontUrl)
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        var result = await Make(handler, frontUrl: frontUrl).RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("not on this computer").And.NotContain(Key);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://localhost:5310")]
    [InlineData("http://127.0.0.1:5310")]
    [InlineData("http://[::1]:5310")]
    public async Task LoopbackAddresses_AreFine(string frontUrl)
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        (await Make(handler, frontUrl: frontUrl).RequestAsync(CancellationToken.None)).Succeeded.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TheKey_IsInNoResultAndNoLogLine_WhateverTheAnswer()
    {
        var outcomes = new List<SleepLockResult>();
        foreach (var status in new[] { 200, 204, 301, 401, 403, 404, 500, 501, 502, 503 })
            outcomes.Add(await Make(new Handler((HttpStatusCode)status)).RequestAsync(CancellationToken.None));
        outcomes.Add(await Make(new Handler(_ => throw new HttpRequestException(Key))).RequestAsync(CancellationToken.None));
        outcomes.Add(await Make(new Handler(_ => throw new TimeoutException(Key))).RequestAsync(CancellationToken.None));
        outcomes.Add(await Make(new Handler(_ => throw new InvalidOperationException(Key))).RequestAsync(CancellationToken.None));

        outcomes.Should().OnlyContain(o => o.Detail == null || !o.Detail.Contains(Key));
        _log.Should().NotBeEmpty().And.OnlyContain(line => !line.Contains(Key));
    }

    // ── the real connection (no fake handler): what goes over the wire, and that a redirect is not followed ────────────────

    [Fact]
    public async Task OverARealConnection_ThePostCarriesTheHeader_AndA204IsLocked()
    {
        using var node = new RawHttpServer("204 No Content");
        var request = new NodeLockRequest(() => $"http://127.0.0.1:{node.Port}", () => Key, log: _log.Enqueue);

        var result = await request.RequestAsync(CancellationToken.None);

        result.Should().Be(new SleepLockResult(true));
        var wire = node.Requests.Should().ContainSingle().Subject;
        wire.Should().StartWith("POST /node/lock HTTP/1.1");
        wire.Should().Contain($"X-Internal-Key: {Key}");
    }

    [Fact]
    public async Task OverARealConnection_ARedirectIsNotFollowed_SoTheKeyCannotBeSentElsewhere()
    {
        using var elsewhere = new RawHttpServer("204 No Content");
        using var node = new RawHttpServer("302 Found", $"Location: http://127.0.0.1:{elsewhere.Port}/node/lock\r\n");
        var request = new NodeLockRequest(() => $"http://127.0.0.1:{node.Port}", () => Key, log: _log.Enqueue);

        var result = await request.RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.HttpStatus.Should().Be(302);
        node.Requests.Should().ContainSingle();
        elsewhere.Requests.Should().BeEmpty("the redirect target never saw the key");
    }

    [Fact]
    public async Task OverARealConnection_NothingListening_IsAFailureWithAFixedText()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var request = new NodeLockRequest(() => $"http://127.0.0.1:{port}", () => Key, log: _log.Enqueue);

        var result = await request.RequestAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().StartWith("The node could not be reached").And.NotContain(Key);
    }

    /// <summary>A bare HTTP/1.1 server on loopback: records each request's head and answers the same status to all of them.</summary>
    private sealed class RawHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _response;

        public RawHttpServer(string status, string extraHeaders = "")
        {
            _response = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n{extraHeaders}\r\n";
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ConcurrentQueue<string> Requests { get; } = new();

        private async Task Loop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[1024];
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0) break;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    Requests.Enqueue(head.ToString());
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_response), _stop.Token);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
