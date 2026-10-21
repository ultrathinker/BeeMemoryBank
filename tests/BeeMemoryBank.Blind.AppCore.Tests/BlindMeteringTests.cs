using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The metering handler in the account's real HTTP pipeline (https and origin checks above it, a fake socket handler below it): what it counts, when it stops a request, and that a stop
/// comes out of the HTTP stack as the typed pause and nothing else.
/// </summary>
public sealed class BlindMeteringTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-metering-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return await send(request, ct);
        }
    }

    private sealed class MutablePolicy : IBlindAccountPolicySource
    {
        public BlindAccountPolicy Current { get; set; } = BlindAccountPolicy.Unlimited;
    }

    private sealed class World : IDisposable
    {
        public required BlindHttpClientProvider Provider { get; init; }
        public required BlindUsageLedger Ledger { get; init; }
        public required MutablePolicy Policy { get; init; }
        public required BlindCallCode Target { get; init; }
        public required LambdaHandler Handler { get; init; }
        public required string Folder { get; init; }
        public HttpClient Client => Provider.GetClient(Target);
        public void Dispose() => Provider.Dispose();
    }

    private World NewWorld(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> serve)
    {
        var clock = new FakeClock(Noon);
        var ledger = new BlindUsageLedger(Path.Combine(_dir, "usage.json"), clock);
        var folder = Path.Combine(_dir, "data");
        Directory.CreateDirectory(folder);
        var stored = new BlindStoredBytes(() => [folder], clock);
        var policy = new MutablePolicy();
        var guard = new BlindLimitGuard(policy, ledger, stored, new BlindDeviceUsage(clock), new FakeBlindDeviceConditions { IsMetered = false, IsPowerSaving = false });
        var handler = new LambdaHandler(serve);
        var entry = new BlindAccountEntry("legacy", BlindAccountLayouts.Root, "x");
        var provider = new BlindHttpClientProvider(new BlindPhoneState(new InMemoryBlindStateStoreFactory().For(entry)), primaryHandlerOverride: () => handler)
        {
            Metering = new BlindMeteringTap(ledger, guard),
        };
        var target = BlindCallCode.Create("https://hub.test:5610", Guid.NewGuid(), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), new byte[32], new byte[32]);
        return new World { Provider = provider, Ledger = ledger, Policy = policy, Target = target, Handler = handler, Folder = folder };
    }

    private static HttpResponseMessage Ok(int bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) };

    private static HttpResponseMessage Streamed(long bytes, Action<long>? served = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(bytes, served)) };
        response.Content.Headers.ContentLength = bytes;
        return response;
    }

    private sealed class ZeroStream(long count, Action<long>? served) : Stream
    {
        private long _position;
        public long Read_ => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => count;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int length) => Read(buffer.AsSpan(offset, length));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, count - _position);
            buffer[..n].Clear();
            _position += n;
            if (n > 0) served?.Invoke(n);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableBytes(byte[] bytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, 700), bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- arithmetic --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BodiesAreCountedExactly_HeadersAndTlsAreEstimated_AndTheNumbersAreWithinTwoPercentOfBodiesPlusOverhead()
    {
        // The node reads the body it is sent (a body is counted by the writes that go out, so a node that did not read it would be sent nothing).
        using var world = NewWorld(async (request, ct) =>
        {
            await request.Content!.ReadAsByteArrayAsync(ct);
            return Ok(50 * 1024);
        });
        using var client = world.Client;

        for (var i = 0; i < 20; i++)
        {
            using var response = await client.PostAsync("https://hub.test:5610/api/sync/x", new ByteArrayContent(new byte[10 * 1024]));
            (await response.Content.ReadAsByteArrayAsync()).Length.Should().Be(50 * 1024);
        }

        var today = world.Ledger.UsageToday();
        today.Requests.Should().Be(20);
        var expectedOut = 20L * (10 * 1024 + BlindMeteringTap.OverheadBytes);
        var expectedIn = 20L * (50 * 1024 + BlindMeteringTap.OverheadBytes);
        today.BytesOut.Should().BeInRange((long)(expectedOut * 0.98), (long)(expectedOut * 1.02));
        today.BytesIn.Should().BeInRange((long)(expectedIn * 0.98), (long)(expectedIn * 1.02));
        today.BytesOut.Should().Be(expectedOut, "the bodies are exact and the estimate is a fixed 1 KB per request");
        today.BytesIn.Should().Be(expectedIn);
    }

    [Fact]
    public async Task ARequestBodyOfUnknownLength_IsCountedAsItIsSent()
    {
        var sent = 0L;
        using var world = NewWorld(async (request, ct) =>
        {
            request.Content!.Headers.ContentLength.Should().BeNull("the body is streamed: its length is not known beforehand");
            sent = (await request.Content.ReadAsByteArrayAsync(ct)).Length;
            return Ok(10);
        });
        using var client = world.Client;
        var body = new byte[10_000];

        using var response = await client.PostAsync("https://hub.test:5610/api/x", new StreamContent(new NonSeekableBytes(body)));

        sent.Should().Be(10_000);
        world.Ledger.UsageToday().BytesOut.Should().Be(10_000 + BlindMeteringTap.OverheadBytes);
    }

    [Fact]
    public async Task AResponseThatIsOnlyPartlyRead_CountsWhatWasRead_NotWhatWasAnnounced()
    {
        var served = 0L;
        using var world = NewWorld((request, ct) => Task.FromResult(Streamed(1L << 20, n => served += n)));
        using var client = world.Client;

        using (var response = await client.GetAsync("https://hub.test:5610/api/blob", HttpCompletionOption.ResponseHeadersRead))
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[100 * 1024];
            var read = 0;
            while (read < buffer.Length) read += await stream.ReadAsync(buffer.AsMemory(read));
        }

        world.Ledger.UsageToday().BytesIn.Should().Be(served + BlindMeteringTap.OverheadBytes);
        world.Ledger.UsageToday().BytesIn.Should().BeLessThan(200 * 1024, "the megabyte that was announced was never read");
    }

    [Fact]
    public async Task TheWrappedResponse_KeepsItsStatus_Headers_AndLengths()
    {
        using var world = NewWorld((request, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[100]), ReasonPhrase = "Partial" };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(900, 999, 5000);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            response.Headers.TryAddWithoutValidation("X-BMB-Package-Sha256", "abc");
            return Task.FromResult(response);
        });
        using var client = world.Client;

        using var response = await client.GetAsync("https://hub.test:5610/api/blind/replica");

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.From.Should().Be(900);
        response.Content.Headers.ContentRange.Length.Should().Be(5000);
        response.Content.Headers.ContentLength.Should().Be(100);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        response.Headers.GetValues("X-BMB-Package-Sha256").Should().Equal("abc");
    }

    [Fact]
    public async Task AJsonResponse_ReadsThroughTheWrapper()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Dictionary<string, int> { ["a"] = 7 }) }));
        using var client = world.Client;

        var value = await client.GetFromJsonAsync<Dictionary<string, int>>("https://hub.test:5610/x");

        value.Should().Equal(new Dictionary<string, int> { ["a"] = 7 });
        world.Ledger.UsageToday().BytesIn.Should().BeGreaterThan(BlindMeteringTap.OverheadBytes);
    }

    // ---- where it sits ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestThatTheHttpsCheckRefuses_IsNeverCounted_BecauseItNeverGoesOut()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(Ok(1)));
        using var client = world.Client;

        var act = () => client.GetAsync("http://hub.test:5610/api/x");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Cleartext*");
        world.Ledger.UsageToday().Requests.Should().Be(0);
        world.Handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ARedirectIsStillRefused_AndWasCounted()
    {
        using var world = NewWorld((request, ct) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.example/");
            return Task.FromResult(redirect);
        });
        using var client = world.Client;

        var act = () => client.GetAsync("https://hub.test:5610/api/x");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*redirect*");
        world.Ledger.UsageToday().Requests.Should().Be(1);
    }

    [Fact]
    public async Task EveryClientTheProviderHandsOut_IsMetered_TheFactoryOnesToo()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(Ok(10)));
        var factoryClient = world.Provider.CreateClient("anything");

        using var response = await factoryClient.GetAsync("https://hub.test:5610/api/x");

        world.Ledger.UsageToday().Requests.Should().Be(1);
    }

    // ---- stopping ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APauseBeforeARequest_StopsItBeforeAnythingGoesOut()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(Ok(10)));
        world.Policy.Current = new BlindAccountPolicy(BlindAccountLimits.None, PausedByUser: true);
        using var client = world.Client;

        var act = () => client.GetAsync("https://hub.test:5610/api/x");

        var thrown = (await act.Should().ThrowAsync<BlindPausedException>()).Which;
        thrown.Pause.Reason.Should().Be(BlindPauseReasons.PausedByYou);
        world.Handler.Calls.Should().Be(0, "nothing reached the network");
        world.Ledger.UsageToday().Requests.Should().Be(0);
        world.Ledger.UsageToday().Traffic.Should().Be(0);
    }

    [Fact]
    public async Task AnExemptClient_CountsButNeverBlocks()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(Ok(10)));
        world.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: 0, MaxRequestsPerDay: 0), PausedByUser: true);
        using var exempt = world.Provider.GetClient(world.Target, exempt: true);

        using var response = await exempt.PostAsync("https://hub.test:5610/api/sync/report-position", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the paused report goes out whatever the limits say");
        world.Ledger.UsageToday().Requests.Should().Be(1, "and it is counted");
        world.Handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AResponseIsCheckedEvery256KB_AndTheStopComesOutOfTheHttpStackAsTheTypedPause()
    {
        var served = 0L;
        using var world = NewWorld((request, ct) => Task.FromResult(Streamed(8L << 20, n => served += n)));
        world.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: 300 * 1024));
        using var client = world.Client;
        var readAtStop = 0L;

        using var response = await client.GetAsync("https://hub.test:5610/api/blob", HttpCompletionOption.ResponseHeadersRead);
        var act = async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0) readAtStop += n;
        };

        var thrown = (await act.Should().ThrowAsync<BlindPausedException>("not rewrapped as an IOException or an HttpRequestException")).Which;

        thrown.Pause.Reason.Should().Be(BlindPauseReasons.Traffic);
        var counted = world.Ledger.UsageToday().Traffic;
        counted.Should().BeGreaterThanOrEqualTo(300 * 1024, "the limit was reached");
        counted.Should().BeLessThanOrEqualTo(300 * 1024 + BlindMeteringTap.CheckEveryBytes + 16 * 1024 + 2 * BlindMeteringTap.OverheadBytes, "and it stopped within one check interval of it");
        served.Should().BeLessThan(1L << 20, "the rest of the 8 MB was never pulled");
    }

    [Fact]
    public async Task TheBufferedReadPaths_LetTheTypedPauseThroughToo()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(Streamed(4L << 20)));
        world.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: 300 * 1024));
        using var client = world.Client;

        // The default GetAsync buffers the whole body inside SendAsync: the stop is thrown from there.
        var buffered = () => client.GetAsync("https://hub.test:5610/api/blob");
        await buffered.Should().ThrowAsync<BlindPausedException>();

        world.Policy.Current = BlindAccountPolicy.Unlimited;
        using var response = await client.GetAsync("https://hub.test:5610/api/blob", HttpCompletionOption.ResponseHeadersRead);
        world.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: world.Ledger.UsageToday().Traffic + 300 * 1024));
        var bytes = () => response.Content.ReadAsByteArrayAsync();
        await bytes.Should().ThrowAsync<BlindPausedException>();
    }

    [Fact]
    public async Task ADownloadThatCannotFitTheStorageLimit_IsRefusedBeforeItsFirstByte()
    {
        var served = 0L;
        using var world = NewWorld((request, ct) => Task.FromResult(Streamed(600 * 1024, n => served += n)));
        File.WriteAllBytes(Path.Combine(world.Folder, "held.bin"), new byte[600 * 1024]);
        world.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = world.Client;

        var act = () => client.GetAsync("https://hub.test:5610/api/blind/replica", HttpCompletionOption.ResponseHeadersRead);

        var thrown = (await act.Should().ThrowAsync<BlindPausedException>()).Which;
        thrown.Pause.Reason.Should().Be(BlindPauseReasons.Disk);
        thrown.Pause.Needed.Should().Be(1200 * 1024, "stored bytes plus the announced length");
        thrown.Pause.Limit.Should().Be(1000 * 1024);
        served.Should().Be(0, "not one byte of the body was read");
        world.Ledger.UsageToday().BytesIn.Should().Be(BlindMeteringTap.OverheadBytes, "only the headers were seen");
    }

    [Fact]
    public async Task ADownloadThatFits_IsNotRefused()
    {
        using var world = NewWorld((request, ct) => Task.FromResult(Streamed(300 * 1024)));
        File.WriteAllBytes(Path.Combine(world.Folder, "held.bin"), new byte[600 * 1024]);
        world.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = world.Client;

        using var response = await client.GetAsync("https://hub.test:5610/api/blind/replica");

        (await response.Content.ReadAsByteArrayAsync()).Length.Should().Be(300 * 1024);
    }

    [Fact]
    public void ThePauseIsNotAnIoException_SoTheHttpStackDoesNotRewrapIt_AndNoInstallCodeTakesItForAFailedPackage()
    {
        var pause = new BlindPausedException(new BlindPause(BlindPauseReasons.Traffic));
        pause.Should().NotBeAssignableTo<IOException>();
        pause.Should().NotBeAssignableTo<InvalidDataException>();
        pause.Should().NotBeAssignableTo<HttpRequestException>();
        pause.Should().NotBeAssignableTo<OperationCanceledException>();
        pause.Message.Should().StartWith("Paused:");
        pause.Message.Should().NotContain("0", "no number, no secret: the type carries the facts");
    }

    [Theory]
    [InlineData(BlindPauseReasons.Traffic, true)]
    [InlineData(BlindPauseReasons.Requests, true)]
    [InlineData(BlindPauseReasons.Disk, true)]
    [InlineData(BlindPauseReasons.DeviceTraffic, true)]
    [InlineData(BlindPauseReasons.DeviceDisk, true)]
    [InlineData(BlindPauseReasons.PausedByYou, true)]
    [InlineData(BlindPauseReasons.MeteredNetwork, false)]
    [InlineData(BlindPauseReasons.PowerSaving, false)]
    [InlineData("something-else", false)]
    public void TheSourceIsToldAboutEveryPause_ButTheNetworkAndTheBattery(string reason, bool reported) =>
        BlindPauseReasons.IsReportable(reason).Should().Be(reported);
}
