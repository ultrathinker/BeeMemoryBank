using System;
using System.Collections.Concurrent;
using System.IO;
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
/// The one client the shell uses to call its own node's front with the internal key (lock on sleep, update-unlock handoff, update guard of
/// a profile switch). These tests are about its guards, which every caller gets: only an http(s) address on this computer is called; the
/// connection uses no proxy and follows no redirect; the wait is bounded; the key is in no text it returns. The callers' own behaviour is
/// tested with them (<see cref="NodeLockRequestTests"/>, the profile switch tests).
/// </summary>
[Collection("BMB_INTERNAL_KEY environment")]
public sealed class NodeFrontClientTests : IDisposable
{
    private const string Key = "front-client-test-key-0123456789abcdef";
    private const string Front = "http://127.0.0.1:5310";

    private readonly string? _savedEnvironmentKey = Environment.GetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-front-client-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, _savedEnvironmentKey);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort, our own temp folder */ }
    }

    private sealed record Seen(string Method, Uri? Uri, string? Key, string? Role);

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Seen> Requests { get; } = new();

        public Handler(HttpStatusCode status, string body = "") : this(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) })) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new Seen(request.Method.Method, request.RequestUri,
                request.Headers.TryGetValues("X-Internal-Key", out var k) ? string.Join(",", k) : null,
                request.Headers.TryGetValues("X-User-Role", out var r) ? string.Join(",", r) : null));
            return await answer(cancellationToken);
        }
    }

    // ── what is sent ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePathIsAppendedToTheFrontAddress_WithOrWithoutATrailingSlash_AndTheKeyIsSent()
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        using var client = new NodeFrontClient(handler);

        var reply = await client.SendAsync(HttpMethod.Post, Front + "/", "/node/lock", Key);

        reply.Answered.Should().BeTrue();
        reply.Status.Should().Be(HttpStatusCode.NoContent);
        reply.IsSuccessStatus.Should().BeTrue();
        var seen = handler.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("POST");
        seen.Uri.Should().Be(new Uri("http://127.0.0.1:5310/node/lock"));
        seen.Key.Should().Be(Key);
        seen.Role.Should().BeNull("the role header is sent only when asked for");
    }

    [Fact]
    public async Task TheSuperadminRole_IsSentOnlyWhenAskedFor_AndAMissingKeySendsNoHeader()
    {
        var handler = new Handler(HttpStatusCode.OK);
        using var client = new NodeFrontClient(handler);

        await client.SendAsync(HttpMethod.Get, Front, "/node/update/status", null, asSuperadmin: true);

        var seen = handler.Requests.Should().ContainSingle().Subject;
        seen.Key.Should().BeNull();
        seen.Role.Should().Be("superadmin");
    }

    [Fact]
    public async Task TheBody_IsReadOnlyWhenAskedFor()
    {
        using var client = new NodeFrontClient(new Handler(HttpStatusCode.OK, "{\"currentStep\":\"Idle\"}"));

        (await client.SendAsync(HttpMethod.Get, Front, "/node/update/status", Key)).Body.Should().BeNull();
        (await client.SendAsync(HttpMethod.Get, Front, "/node/update/status", Key, readBody: true)).Body.Should().Be("{\"currentStep\":\"Idle\"}");
    }

    // ── the guards ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://192.0.2.5:5310")]
    [InlineData("http://10.0.0.7:5310")]
    [InlineData("http://[2001:db8::1]:5310")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task AnAddressNotOnThisComputer_IsRefused_AndNothingIsSent(string frontUrl)
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        using var client = new NodeFrontClient(handler);

        var reply = await client.SendAsync(HttpMethod.Post, frontUrl, "/node/lock", Key);

        reply.Failure.Should().Be(NodeFrontFailure.NotLoopback);
        reply.Answered.Should().BeFalse();
        handler.Requests.Should().BeEmpty("the key must never leave this computer");
    }

    [Theory]
    [InlineData("http://127.0.0.1:5310")]
    [InlineData("http://localhost:5310")]
    [InlineData("https://[::1]:5311")]
    public async Task LoopbackAddresses_AreAccepted(string frontUrl)
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        using var client = new NodeFrontClient(handler);

        (await client.SendAsync(HttpMethod.Post, frontUrl, "/node/lock", Key)).Answered.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void ARealConnection_UsesNoProxy_AndFollowsNoRedirect()
    {
        var handler = NodeFrontClient.CreateDefaultHandler();

        handler.UseProxy.Should().BeFalse("a system proxy must never see the key");
        handler.AllowAutoRedirect.Should().BeFalse("a redirect must never carry the key to another address");
    }

    [Fact]
    public async Task OverARealConnection_ARedirectIsNotFollowed_SoTheKeyCannotBeSentElsewhere()
    {
        using var elsewhere = new RawHttpServer("204 No Content");
        using var node = new RawHttpServer("302 Found", $"Location: http://127.0.0.1:{elsewhere.Port}/node/lock\r\n");
        using var client = new NodeFrontClient();

        var reply = await client.SendAsync(HttpMethod.Post, $"http://127.0.0.1:{node.Port}", "/node/lock", Key);

        reply.Status.Should().Be(HttpStatusCode.Found);
        node.Requests.Should().ContainSingle().Which.Should().Contain($"X-Internal-Key: {Key}");
        elsewhere.Requests.Should().BeEmpty("the redirect target never saw the key");
    }

    [Fact]
    public async Task TheWaitIsBounded_ANodeThatNeverAnswersIsATimeout()
    {
        var handler = new Handler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new NodeFrontClient(handler, TimeSpan.FromMilliseconds(200));

        var started = DateTime.UtcNow;
        var reply = await client.SendAsync(HttpMethod.Post, Front, "/node/lock", Key);

        reply.Failure.Should().Be(NodeFrontFailure.TimedOut);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(200));
        new NodeFrontClient().Timeout.Should().Be(NodeFrontClient.DefaultTimeout).And.Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ACancelledCaller_IsToldApartFromATimeout()
    {
        var handler = new Handler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new NodeFrontClient(handler, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var reply = await client.SendAsync(HttpMethod.Post, Front, "/node/lock", Key, cancellationToken: cts.Token);

        reply.Failure.Should().Be(NodeFrontFailure.Cancelled);
    }

    [Fact]
    public async Task TheKey_IsInNothingTheClientReturns_WhateverGoesWrong()
    {
        var replies = new[]
        {
            await Send(new Handler(_ => throw new HttpRequestException(Key))),
            await Send(new Handler(_ => throw new InvalidOperationException("boom " + Key))),
            await Send(new Handler(_ => throw new TaskCanceledException(Key))),
            await Send(new Handler(HttpStatusCode.Unauthorized, Key)),
        };

        replies.Should().OnlyContain(r => !(r.ErrorKind ?? "").Contains(Key) && !(r.ErrorMessage ?? "").Contains(Key));
        replies[0].Failure.Should().Be(NodeFrontFailure.Unreachable);
        replies[0].ErrorMessage.Should().Be("[key]", "the text is kept for logs, with the key replaced");
        replies[0].ErrorKind.Should().NotContain(Key, "the kind of a failure is reported without the exception's text");
        replies[1].Failure.Should().Be(NodeFrontFailure.Failed);
        replies[1].ErrorKind.Should().Be(nameof(InvalidOperationException));
        replies[1].ErrorMessage.Should().Be("boom [key]");

        static async Task<NodeFrontReply> Send(Handler handler)
        {
            using var client = new NodeFrontClient(handler);
            return await client.SendAsync(HttpMethod.Post, Front, "/node/lock", Key);
        }
    }

    [Fact]
    public void Scrub_ReplacesEveryOccurrence_AndLeavesTextAloneWithoutAKey()
    {
        NodeFrontClient.Scrub($"{Key} and {Key}", Key).Should().Be("[key] and [key]");
        NodeFrontClient.Scrub("nothing here", null).Should().Be("nothing here");
        NodeFrontClient.Scrub("nothing here", "").Should().Be("nothing here");
    }

    // ── where a key can come from ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEnvironmentKeyComesFirst_ThenTheKeyFileOfTheDataDirectory_ThenNothing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ".internal-key"), "  file-key\r\n");

        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, "env-key");
        NodeFrontClient.KeyFromEnvironment().Should().Be("env-key");
        NodeFrontClient.ResolveInternalKey(_dir).Should().Be("env-key");

        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, null);
        NodeFrontClient.KeyFromEnvironment().Should().BeNull();
        NodeFrontClient.ResolveInternalKey(_dir).Should().Be("file-key", "the file's text is trimmed");
        NodeFrontClient.ResolveInternalKey(Path.Combine(_dir, "missing")).Should().BeNull();
        NodeFrontClient.ResolveInternalKey(null).Should().BeNull();

        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, "");
        NodeFrontClient.KeyFromEnvironment().Should().BeNull("an empty variable is no key");
    }

    // ── the update-unlock handoff, which now goes through the same client ─────────────────────────────────────

    [Fact]
    public async Task TheSessionHandoff_PostsWithTheKeyAndTheRole_ToTheOpenNode_AndA2xxIsSuccess()
    {
        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, Key);
        using var node = new RawHttpServer("204 No Content");

        (await NodeSessionHandoff.RequestAsync($"http://127.0.0.1:{node.Port}/")).Should().BeTrue();

        var wire = node.Requests.Should().ContainSingle().Subject;
        wire.Should().StartWith("POST /node/update/unlock-handoff HTTP/1.1");
        wire.Should().Contain($"X-Internal-Key: {Key}").And.Contain("X-User-Role: superadmin");
    }

    [Fact]
    public async Task TheSessionHandoff_SendsNothing_WithoutAKey_OrToAnAddressNotOnThisComputer()
    {
        using var node = new RawHttpServer("204 No Content");

        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, null);
        (await NodeSessionHandoff.RequestAsync($"http://127.0.0.1:{node.Port}")).Should().BeFalse("this app did not start the node");
        node.Requests.Should().BeEmpty();

        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, Key);
        var started = DateTime.UtcNow;
        (await NodeSessionHandoff.RequestAsync("http://192.0.2.5:5310")).Should().BeFalse();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "the request is refused up front, not attempted");
    }

    [Fact]
    public async Task TheSessionHandoff_AFailedNode_IsFalse()
    {
        Environment.SetEnvironmentVariable(NodeFrontClient.KeyEnvironmentVariable, Key);
        using var node = new RawHttpServer("500 Internal Server Error");

        (await NodeSessionHandoff.RequestAsync($"http://127.0.0.1:{node.Port}")).Should().BeFalse();
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
