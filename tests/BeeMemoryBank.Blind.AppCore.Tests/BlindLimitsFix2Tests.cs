using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 2 of the limits: what the limits do when time, other accounts, other threads and untrusted lengths are in play. Every test here goes through the account's real HTTP pipeline
/// (or the guard, for a decision) and states its expectation as an outcome, so that it can be run against the code of fix round 1 and fail there:
/// storage that is live (several blobs within one minute), usage files that exist and cannot be trusted (a damaged file is not a fresh budget), the decision and the charge as ONE step
/// (two callers racing on the last request, two accounts racing on the device's last bytes), a stored size that cannot be measured (unknown is not zero), lengths nobody can trust
/// (near <c>long.MaxValue</c>, missing, understated), reads that never pass a 256 KB check, request bodies counted by the writes that succeed, and sizes that overflow.
/// </summary>
public sealed partial class BlindLimitsFix2Tests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Url = "https://hub.test:5610/api/x";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-fix2-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _undo = [];

    public void Dispose()
    {
        foreach (var undo in _undo) undo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    /// <summary>
    /// A device whose answer to "is the network metered" only comes once <paramref name="parties"/> callers have asked (or half a second has passed): it holds every caller that is inside the
    /// check at the same moment, which is the moment a check-then-charge decision is wrong. Code that decides and charges in one step under one lock cannot have two callers inside at once;
    /// the first waits out the half second alone, and the second then sees what the first took.
    /// </summary>
    private sealed class RendezvousConditions(int parties) : IBlindDeviceConditions
    {
        private readonly ManualResetEventSlim _met = new(false);
        private int _arrived;

        public bool? IsMetered
        {
            get
            {
                if (Interlocked.Increment(ref _arrived) >= parties) _met.Set();
                _met.Wait(TimeSpan.FromMilliseconds(500));
                return false;
            }
        }

        public bool? IsPowerSaving => false;
    }

    private sealed class Rig : IDisposable
    {
        public required BlindHttpClientProvider Provider { get; init; }
        public required BlindUsageLedger Ledger { get; init; }
        public required BlindStoredBytes Stored { get; init; }
        public required BlindDeviceUsage Device { get; init; }
        public required BlindLimitGuard Guard { get; init; }
        public required MutablePolicy Policy { get; init; }
        public required BlindCallCode Target { get; init; }
        public required LambdaHandler Handler { get; init; }
        public required FakeClock Clock { get; init; }
        public required string Folder { get; init; }
        public required string UsageFile { get; init; }
        public HttpClient Client => Provider.GetClient(Target);
        public void Dispose() => Provider.Dispose();
    }

    private Rig NewRig(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> serve,
        string name = "a", BlindDeviceUsage? device = null, IBlindDeviceConditions? conditions = null, FakeClock? clock = null, Func<IEnumerable<string>>? roots = null)
    {
        clock ??= new FakeClock(Noon);
        var folder = Path.Combine(_dir, name, "data");
        Directory.CreateDirectory(folder);
        var usageFile = Path.Combine(_dir, name, "usage.json");
        var ledger = new BlindUsageLedger(usageFile, clock);
        var stored = new BlindStoredBytes(roots ?? (() => [folder]), clock);
        device ??= new BlindDeviceUsage(clock);
        device.Register(name, ledger, stored);
        var policy = new MutablePolicy();
        var guard = new BlindLimitGuard(policy, ledger, stored, device, conditions ?? new FakeBlindDeviceConditions { IsMetered = false, IsPowerSaving = false });
        var handler = new LambdaHandler(serve);
        var entry = new BlindAccountEntry("legacy", BlindAccountLayouts.Root, "x");
        var provider = new BlindHttpClientProvider(new BlindPhoneState(new InMemoryBlindStateStoreFactory().For(entry)), primaryHandlerOverride: () => handler)
        {
            Metering = new BlindMeteringTap(ledger, guard),
        };
        var target = BlindCallCode.Create("https://hub.test:5610", Guid.NewGuid(), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), new byte[32], new byte[32]);
        return new Rig
        {
            Provider = provider, Ledger = ledger, Stored = stored, Device = device, Guard = guard, Policy = policy, Target = target, Handler = handler, Clock = clock,
            Folder = folder, UsageFile = usageFile,
        };
    }

    private static HttpResponseMessage Ok(int bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) };

    /// <summary>A response whose body is <paramref name="bytes"/> zeros; <paramref name="declared"/> is what its Content-Length header says (null: no length, as in a chunked answer).</summary>
    private static HttpResponseMessage Streamed(long bytes, long? declared, Action<long>? served = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(bytes, served)) };
        if (declared is { } length) response.Content.Headers.ContentLength = length;
        return response;
    }

    private sealed class ZeroStream(long count, Action<long>? served) : Stream
    {
        private long _position;
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

    /// <summary>Reads a streamed answer to its end (or to the pause that stops it) in <paramref name="bufferSize"/> reads; returns the pause's reason or null, and the biggest read.</summary>
    private static async Task<(string? Reason, long Read, int Biggest)> DrainAsync(HttpClient client, string url, int bufferSize)
    {
        long read = 0;
        var biggest = 0;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[bufferSize];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                read += n;
                biggest = Math.Max(biggest, n);
            }
        }
        catch (BlindPausedException pause)
        {
            return (pause.Pause.Reason, read, biggest);
        }
        return (null, read, biggest);
    }

    private static async Task<string?> ReasonOfAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (BlindPausedException pause)
        {
            return pause.Pause.Reason;
        }
    }

    // ---- 1. storage is live ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SeveralBlobsWithinAMinute_StopAtTheStorageLimit_NotAMinuteLater()
    {
        // Each blob is read and kept in memory here (nothing is written to the account's folder): the meter alone knows what arrived. A decision made on a walk of the folders that is up to
        // a minute old would let every one of them in.
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(300 * 1024, 300 * 1024, n => Interlocked.Add(ref served, n))));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = rig.Client;

        var reasons = new List<string?>();
        for (var i = 0; i < 6; i++)
        {
            reasons.Add(await ReasonOfAsync(async () =>
            {
                using var response = await client.GetAsync(Url);
                (await response.Content.ReadAsByteArrayAsync()).Length.Should().Be(300 * 1024);
            }));
        }

        reasons.Take(3).Should().AllBeEquivalentTo((string?)null, "three blobs of 300 KB fit in 1000 KB");
        reasons[3].Should().Be(BlindPauseReasons.Disk, "the fourth cannot fit what the first three already brought in");
        reasons.Skip(3).Should().AllBeEquivalentTo(BlindPauseReasons.Disk);
        Interlocked.Read(ref served).Should().Be(3 * 300 * 1024, "not a byte of a blob that does not fit was read");
    }

    [Fact]
    public async Task AResponseOfUnknownLength_IsHeldToTheRoomThatIsLeft()
    {
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(5L << 20, declared: null, n => Interlocked.Add(ref served, n))));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = rig.Client;

        var (reason, read, _) = await DrainAsync(client, Url, 64 * 1024);

        reason.Should().Be(BlindPauseReasons.Disk, "a chunked answer that is longer than the room is stopped at the room");
        read.Should().BeLessThanOrEqualTo(1000 * 1024);
        Interlocked.Read(ref served).Should().BeLessThanOrEqualTo(1000 * 1024, "the stream was never read past the limit");
        rig.Ledger.UsageToday().BytesIn.Should().BeLessThanOrEqualTo(1000 * 1024 + BlindMeteringTap.OverheadBytes);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(10L)]
    public async Task AnUnderstatedLength_ZeroOrSmall_IsHeldToTheRoomToo(long declared)
    {
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(5L << 20, declared, n => Interlocked.Add(ref served, n))));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = rig.Client;

        var (reason, _, _) = await DrainAsync(client, Url, 64 * 1024);

        reason.Should().Be(BlindPauseReasons.Disk, "the header said " + declared + ", the stream did not stop there");
        Interlocked.Read(ref served).Should().BeLessThanOrEqualTo(1000 * 1024);
    }

    [Fact]
    public async Task ADeviceStorageLimit_IsLiveToo_AcrossTwoAccounts()
    {
        // Two accounts of one device: what the first brings in is room the second no longer has, at once.
        var clock = new FakeClock(Noon);
        var limits = new BlindDeviceLimits(MaxStoredBytes: 1000 * 1024);
        var device = new BlindDeviceUsage(clock, null, () => limits);
        using var first = NewRig((request, ct) => Task.FromResult(Streamed(400 * 1024, 400 * 1024)), "a", device, clock: clock);
        using var second = NewRig((request, ct) => Task.FromResult(Streamed(400 * 1024, 400 * 1024)), "b", device, clock: clock);
        using var firstClient = first.Client;
        using var secondClient = second.Client;

        var reasons = new List<string?>
        {
            await ReasonOfAsync(async () => (await firstClient.GetAsync(Url)).Dispose()),
            await ReasonOfAsync(async () => (await secondClient.GetAsync(Url)).Dispose()),
            await ReasonOfAsync(async () => (await firstClient.GetAsync(Url)).Dispose()),
        };

        reasons[0].Should().BeNull();
        reasons[1].Should().BeNull();
        reasons[2].Should().Be(BlindPauseReasons.DeviceDisk, "800 KB are held between the two accounts, and a third blob of 400 KB would pass the 1000 KB of the device");
    }

    // ---- 2. usage that exists and cannot be trusted is not a fresh budget ------------------------------------------------------------

    private static string ValidUsage(FakeClock clock, string file)
    {
        var ledger = new BlindUsageLedger(file, clock);
        for (var i = 0; i < 150; i++) ledger.AddRequest();
        ledger.AddIn(5000);
        ledger.Flush();
        return File.ReadAllText(file);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":1,\"highWater\":\"not a date\",\"days\":[]}")]
    public void AMalformedUsageFile_PausesAnAccountThatHasATrafficOrRequestLimit_AndIsKeptAsItWas(string content)
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), clock: clock);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200));

        var pause = rig.Guard.Check();
        rig.Ledger.AddRequest();
        rig.Ledger.Flush();

        pause.Should().NotBeNull("a damaged file is not an empty day: the limit cannot be checked against it");
        pause!.Reason.Should().Be("usage-unavailable");
        File.ReadAllText(file).Should().Be(content, "the damaged file is evidence: it is never overwritten");
    }

    [Fact]
    public void AUsageFileOfAnotherVersion_IsTreatedLikeADamagedOne()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var valid = ValidUsage(clock, file);
        var otherVersion = Regex.Replace(valid, "\"version\"\\s*:\\s*1", "\"version\": 2", RegexOptions.IgnoreCase);
        otherVersion.Should().NotBe(valid, "the test file must really carry another version");
        File.WriteAllText(file, otherVersion);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), clock: clock);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: 10 << 20));

        var pause = rig.Guard.Check();

        pause.Should().NotBeNull();
        pause!.Reason.Should().Be("usage-unavailable");
        File.ReadAllText(file).Should().Be(otherVersion);
    }

    [Fact]
    public void AUsageFileThatCannotBeOpened_PausesWhileItIsLocked_AndTheCountsComeBackWhenItIsReleased()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        ValidUsage(clock, file);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), clock: clock);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200));

        BlindPause? whileLocked;
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            whileLocked = rig.Guard.Check();
        }

        whileLocked.Should().NotBeNull("an access error is not 'no file': the day's count is unknown");
        whileLocked!.Reason.Should().Be("usage-unavailable");

        clock.Advance(TimeSpan.FromSeconds(10));
        rig.Guard.Check().Should().BeNull("released: the 150 requests are counted again, and 150 is below 200");
        rig.Ledger.UsageToday().Requests.Should().Be(150, "the account's own count came back, not a fresh zero");
    }

    [Fact]
    public void AMissingUsageFile_IsAFreshInstall_NotAProblem()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200, MaxTrafficBytesPerDay: 1 << 20));

        rig.Guard.Check().Should().BeNull();
        rig.Ledger.UsageToday().Requests.Should().Be(0);
    }

    [Fact]
    public void ADamagedDeviceFile_WithTheClockSetBack_DoesNotRefillTheDevicesBudget()
    {
        var clock = new FakeClock(Noon);
        var stateFile = Path.Combine(_dir, "device-usage.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(stateFile, "{ damaged");
        var limits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024);
        var device = new BlindDeviceUsage(clock, stateFile, () => limits);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), device: device, clock: clock);

        var before = rig.Guard.Check();
        clock.Set(Noon.AddDays(-3));
        var afterClockSetBack = rig.Guard.Check();

        before.Should().NotBeNull();
        before!.Reason.Should().Be("device-usage-unavailable");
        afterClockSetBack.Should().NotBeNull("with the high-water date lost, a clock set back must not read as a new, empty day");
        afterClockSetBack!.Reason.Should().Be("device-usage-unavailable");
        File.ReadAllText(stateFile).Should().Be("{ damaged", "never overwritten");
    }

    [Fact]
    public async Task AccountsThatHaveNoLimitThatDependsOnUsage_AreNotStoppedByDamagedCounts()
    {
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ damaged");
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(5)));
        using var client = rig.Client;

        using var response = await client.GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "nothing is checked against the counts, so nothing can be wrong with them");
    }

    // ---- 3. the decision and the charge are one step -------------------------------------------------------------------------------------

    private static async Task<List<string?>> RaceAsync(params HttpClient[] clients)
    {
        using var go = new ManualResetEventSlim(false);
        var tasks = clients.Select(client => Task.Run(async () =>
        {
            go.Wait();
            return await ReasonOfAsync(async () => (await client.GetAsync(Url)).Dispose());
        })).ToArray();
        go.Set();
        return [.. await Task.WhenAll(tasks)];
    }

    [Fact]
    public async Task TwoCallersOfOneAccount_RacingOnThe200thRequest_AdmitExactlyOne()
    {
        // 199 of 200 requests are used. Both callers are inside the check at the same moment (the device's answer waits for both): a check that only looks lets both through.
        var parties = new RendezvousConditions(2);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), conditions: parties);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200), UnmeteredOnly: true);
        for (var i = 0; i < 199; i++) rig.Ledger.AddRequest();
        using var one = rig.Client;
        using var two = rig.Client;

        var reasons = await RaceAsync(one, two);

        reasons.Count(r => r is null).Should().Be(1, "one request was left");
        reasons.Count(r => r == BlindPauseReasons.Requests).Should().Be(1);
        rig.Ledger.UsageToday().Requests.Should().Be(200, "never over the limit");
        rig.Handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TwoAccountsRacingOnTheDevicesLastBytes_NeverGoOverTheDevicesLimit()
    {
        // The device may use 10 KB a day and 7 KB are used. A request costs 2 KB (1 KB of headers each way): one fits, a second would make 11 KB. Two accounts ask at the same moment.
        var clock = new FakeClock(Noon);
        var limits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024);
        var device = new BlindDeviceUsage(clock, null, () => limits);
        var parties = new RendezvousConditions(2);
        using var first = NewRig((request, ct) => Task.FromResult(Ok(10)), "a", device, parties, clock);
        using var second = NewRig((request, ct) => Task.FromResult(Ok(10)), "b", device, parties, clock);
        first.Policy.Current = new BlindAccountPolicy(BlindAccountLimits.None, UnmeteredOnly: true);
        second.Policy.Current = new BlindAccountPolicy(BlindAccountLimits.None, UnmeteredOnly: true);
        first.Ledger.AddIn(7 * 1024);
        using var firstClient = first.Client;
        using var secondClient = second.Client;

        var reasons = await RaceAsync(firstClient, secondClient);

        device.TrafficToday().Should().BeLessThanOrEqualTo(10 * 1024, "never over the limit");
        reasons.Count(r => r is null).Should().Be(1, "room for one request, not two");
        reasons.Count(r => r == BlindPauseReasons.DeviceTraffic).Should().Be(1);
    }

    [Fact]
    public async Task TwoAccountsRacingOnTheLast1KBOfADevice_AreBothRefused_AndTheDeviceStaysUnderItsLimit()
    {
        var clock = new FakeClock(Noon);
        var limits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024);
        var device = new BlindDeviceUsage(clock, null, () => limits);
        var parties = new RendezvousConditions(2);
        using var first = NewRig((request, ct) => Task.FromResult(Ok(10)), "a", device, parties, clock);
        using var second = NewRig((request, ct) => Task.FromResult(Ok(10)), "b", device, parties, clock);
        first.Policy.Current = new BlindAccountPolicy(BlindAccountLimits.None, UnmeteredOnly: true);
        second.Policy.Current = new BlindAccountPolicy(BlindAccountLimits.None, UnmeteredOnly: true);
        first.Ledger.AddIn(9 * 1024);
        using var firstClient = first.Client;
        using var secondClient = second.Client;

        var reasons = await RaceAsync(firstClient, secondClient);

        device.TrafficToday().Should().BeLessThanOrEqualTo(10 * 1024, "9 KB of 10 KB used: a request of 2 KB does not fit, for either account");
        reasons.Should().AllBeEquivalentTo(BlindPauseReasons.DeviceTraffic);
    }

    [Fact]
    public async Task ManyCallersAtOnce_NeverAdmitMoreRequestsThanTheLimit()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200));
        using var client = rig.Client;
        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                if (await ReasonOfAsync(async () => (await client.GetAsync(Url)).Dispose()) is null) Interlocked.Increment(ref admitted);
            }
        })));

        admitted.Should().Be(200);
        rig.Ledger.UsageToday().Requests.Should().Be(200);
    }

    // ---- 5. a stored size that cannot be measured is unknown, not zero ------------------------------------------------------------

    [Fact]
    public void AnInaccessibleRoot_IsAnUnknownSize_AndPausesAnAccountWithAStorageLimit()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        File.WriteAllBytes(Path.Combine(rig.Folder, "held.bin"), new byte[600 * 1024]);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        var denied = TestAccess.DenyListing(rig.Folder);
        if (denied is null) return; // this machine cannot take the right to list a folder away from this process (it is allowed anyway): nothing to show here
        _undo.Add(denied);

        var pause = rig.Guard.Check();

        pause.Should().NotBeNull("a folder that cannot be read is not an empty folder");
        pause!.Reason.Should().Be("storage-unknown");
    }

    [Fact]
    public void ARootThatTheFileSystemWillNotExamine_IsAnUnknownSize_NotZero()
    {
        // A name that no file system accepts (one component of 400 characters) cannot be examined, so what it holds cannot be known: that is not "nothing is there".
        var root = Path.Combine(_dir, "a", new string('x', 400));
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), roots: () => [root]);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));

        var pause = rig.Guard.Check();

        pause.Should().NotBeNull();
        pause!.Reason.Should().Be("storage-unknown");
    }

    [Fact]
    public void ARootThatIsALink_IsNotMeasuredThroughTheLink()
    {
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[1500 * 1024]);
        var link = Path.Combine(_dir, "a", "data-link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TestLinks.TryCreateDirectoryLink(link, target)) return; // links cannot be made here
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), roots: () => [link]);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 2000 * 1024));

        var pause = rig.Guard.Check();

        pause.Should().NotBeNull("what lies behind a link is not what the account owns, and the account cannot say how much it holds");
        pause!.Reason.Should().Be("storage-unknown");
    }

    [Fact]
    public void ALinkInsideTheOwnedData_MakesTheSizeUnknown_NotSmaller()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        File.WriteAllBytes(Path.Combine(rig.Folder, "held.bin"), new byte[600 * 1024]);
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[800 * 1024]);
        if (!TestLinks.TryCreateDirectoryLink(Path.Combine(rig.Folder, "sneaky"), target)) return;
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));

        var pause = rig.Guard.Check();

        pause.Should().NotBeNull("the link hides a size that would not fit: skipping it must not read as 600 KB, which would");
        pause!.Reason.Should().Be("storage-unknown");
    }

    // ---- 6. a length nobody can trust --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 5)]
    [InlineData(long.MaxValue - 600 * 1024 + 1)]
    public async Task AnAbsurdLength_IsRefusedBeforeTheFirstByte_AndNeededNeverWraps(long declared)
    {
        // With 600 KB held, stored + declared wraps to a small or negative number for each of these: a sum that overflows must be a refusal, not a pass.
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(10, declared, n => Interlocked.Add(ref served, n))));
        File.WriteAllBytes(Path.Combine(rig.Folder, "held.bin"), new byte[600 * 1024]);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = rig.Client;

        var act = () => client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);

        var thrown = (await act.Should().ThrowAsync<BlindPausedException>()).Which;
        thrown.Pause.Reason.Should().Be(BlindPauseReasons.Disk);
        thrown.Pause.Needed.Should().Be(long.MaxValue, "saturated: the sum of what is held and what is announced does not fit in a long");
        Interlocked.Read(ref served).Should().Be(0);
    }

    [Fact]
    public async Task AZeroLengthAnswer_IsNotRefused()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(0, 0)));
        File.WriteAllBytes(Path.Combine(rig.Folder, "held.bin"), new byte[600 * 1024]);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxStoredBytes: 1000 * 1024));
        using var client = rig.Client;

        using var response = await client.GetAsync(Url);

        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAbsurdLength_AgainstATrafficLimit_StopsAtTheLimit_NotAtAWrappedNumber()
    {
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(5L << 20, long.MaxValue - 10, n => Interlocked.Add(ref served, n))));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: 300 * 1024));
        using var client = rig.Client;

        var (reason, _, _) = await DrainAsync(client, Url, 32 * 1024);

        reason.Should().Be(BlindPauseReasons.Traffic);
        rig.Ledger.UsageToday().Traffic.Should().BeLessThanOrEqualTo(300 * 1024);
    }

    // ---- 7. the 256 KB check is a hard boundary; a request body is what was written ----------------------------------------------------

    [Fact]
    public async Task ALargeBufferRead_NeverReturnsMoreThanTheNext256KBCheck()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(4L << 20, 4L << 20)));
        using var client = rig.Client;

        var (reason, read, biggest) = await DrainAsync(client, Url, 1 << 20);

        reason.Should().BeNull();
        read.Should().Be(4L << 20);
        biggest.Should().BeLessThanOrEqualTo(BlindMeteringTap.CheckEveryBytes, "a read of a megabyte is cut at the next check, so that no read carries the answer past a limit");
    }

    [Fact]
    public async Task ALargeBufferRead_CannotRunPastTheTrafficLimit()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(4L << 20, 4L << 20)));
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxTrafficBytesPerDay: 300 * 1024));
        using var client = rig.Client;

        var (reason, _, _) = await DrainAsync(client, Url, 1 << 20);

        reason.Should().Be(BlindPauseReasons.Traffic);
        rig.Ledger.UsageToday().Traffic.Should().BeLessThanOrEqualTo(300 * 1024, "the read was cut to what the day had left");
    }

    [Fact]
    public async Task AReadThatCrossesSeveral256KBBoundaries_IsStillCheckedAtEachOne()
    {
        // The person pauses the account while a big answer is coming in: with reads that are cut at the boundary, the pause is seen within one boundary of the change.
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(8L << 20, 8L << 20, n => Interlocked.Add(ref served, n))));
        using var client = rig.Client;
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1 << 20];
        long read = 0;
        for (var i = 0; i < 2; i++) read += await stream.ReadAsync(buffer);

        rig.Policy.Current = new BlindAccountPolicy(BlindAccountLimits.None, PausedByUser: true);
        var act = async () =>
        {
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0) read += n;
        };

        await act.Should().ThrowAsync<BlindPausedException>();
        read.Should().BeLessThanOrEqualTo(3L * BlindMeteringTap.CheckEveryBytes, "the pause was seen at the first check after it was set");
    }

    /// <summary>A request body that announces <paramref name="announced"/> bytes and writes what <paramref name="write"/> does.</summary>
    private sealed class ScriptedContent(long announced, Func<Stream, CancellationToken, Task> write) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => write(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = announced;
            return announced >= 0;
        }
    }

    [Fact]
    public async Task ARequestBodyThatTheNodeNeverReads_IsNotCounted_ForItsHeaderLength()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        using var client = rig.Client;

        using var response = await client.PostAsync(Url, new ByteArrayContent(new byte[10 * 1024]));

        rig.Ledger.UsageToday().BytesOut.Should().Be(BlindMeteringTap.OverheadBytes, "the body was not written to the wire: only the headers went out");
    }

    [Fact]
    public async Task ARequestBodyWhoseHeaderLies_IsCountedForWhatWasWritten()
    {
        using var rig = NewRig(async (request, ct) =>
        {
            await request.Content!.ReadAsByteArrayAsync(ct);
            return Ok(1);
        });
        using var client = rig.Client;
        var body = new ScriptedContent(1 << 20, (stream, ct) => stream.WriteAsync(new byte[10], ct).AsTask());

        using var response = await client.PostAsync(Url, body);

        rig.Ledger.UsageToday().BytesOut.Should().Be(10 + BlindMeteringTap.OverheadBytes, "ten bytes went out, whatever the header said");
    }

    [Fact]
    public async Task ARequestBodyWithNoHeaderLength_IsCountedByItsWrites()
    {
        using var rig = NewRig(async (request, ct) =>
        {
            await request.Content!.ReadAsByteArrayAsync(ct);
            return Ok(1);
        });
        using var client = rig.Client;
        var body = new ScriptedContent(-1, async (stream, ct) =>
        {
            for (var i = 0; i < 5; i++) await stream.WriteAsync(new byte[2000], ct);
        });

        using var response = await client.PostAsync(Url, body);

        rig.Ledger.UsageToday().BytesOut.Should().Be(10_000 + BlindMeteringTap.OverheadBytes);
    }

    [Fact]
    public async Task ARequestBodyThatFailsHalfWay_IsCountedForTheBytesThatWereWritten()
    {
        using var rig = NewRig(async (request, ct) =>
        {
            await request.Content!.ReadAsByteArrayAsync(ct);
            return Ok(1);
        });
        using var client = rig.Client;
        var body = new ScriptedContent(10 * 1024, async (stream, ct) =>
        {
            await stream.WriteAsync(new byte[4096], ct);
            throw new IOException("the connection went away");
        });

        var act = () => client.PostAsync(Url, body);

        await act.Should().ThrowAsync<Exception>();
        rig.Ledger.UsageToday().BytesOut.Should().Be(4096 + BlindMeteringTap.OverheadBytes, "what was not written was not sent, and is not counted");
    }

    [Fact]
    public async Task ARequestThatIsCancelledWhileItsBodyIsGoingOut_IsCountedForTheBytesThatWentOut()
    {
        var wrote = new TaskCompletionSource();
        using var rig = NewRig(async (request, ct) =>
        {
            await request.Content!.ReadAsByteArrayAsync(ct);
            return Ok(1);
        });
        using var client = rig.Client;
        using var cancel = new CancellationTokenSource();
        var body = new ScriptedContent(1 << 20, async (stream, ct) =>
        {
            await stream.WriteAsync(new byte[2048], ct);
            wrote.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        var send = client.PostAsync(Url, body, cancel.Token);
        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        var act = () => send;

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Ledger.UsageToday().BytesOut.Should().Be(2048 + BlindMeteringTap.OverheadBytes, "a megabyte was announced and 2 KB went out");
    }

    // ---- 8. a size that overflows is a sentence, not an exception ------------------------------------------------------------------------

    public static TheoryData<string> OverflowingSizes() =>
    [
        "9" + new string('9', 300) + " GB",
        "9" + new string('9', 40) + " MB",
        "1" + new string('0', 30) + ".5 GB",
        "79228162514264337593543950336 KB",
        "99999999999999999999 B",
        new string('9', 600) + " TB",
    ];

    [Theory]
    [MemberData(nameof(OverflowingSizes))]
    public void ASizeThatOverflows_IsRefusedWithTheMessageOfAnyOtherTooBigSize_AndNeverThrows(string text)
    {
        var parse = () => BlindSizeFormat.TryParse(text, out _, out _);
        parse.Should().NotThrow();

        BlindSizeFormat.TryParse(text, out var bytes, out var problem).Should().BeFalse();

        bytes.Should().Be(0);
        problem.Should().Contain("more than a device can hold");
    }

    [Theory]
    [MemberData(nameof(OverflowingSizes))]
    public void TheLimitsForm_SaysSoInsteadOfThrowing(string text)
    {
        var validate = () => new BlindLimitsForm(text, "", "", false, false).Validate();
        validate.Should().NotThrow();

        var result = validate();

        result.Ok.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Should().Contain("more than a device can hold");
    }

    [Theory]
    [MemberData(nameof(OverflowingSizes))]
    public void TheDeviceLimitsForm_SaysSoInsteadOfThrowing(string text)
    {
        var validate = () => new BlindDeviceLimitsForm(text, text).Validate();
        validate.Should().NotThrow();

        var (limits, problems) = validate();

        limits.Should().BeNull();
        problems.Should().OnlyContain(p => p.Contains("more than a device can hold"));
        problems.Should().NotBeEmpty();
    }

    [Fact]
    public void TheLargestSizeThatIsAccepted_IsExactlyTheBound_AndOneMoreIsRefused()
    {
        BlindSizeFormat.TryParse("1024 TB", out var bytes, out var problem).Should().BeTrue(problem);
        bytes.Should().Be(BlindSizeFormat.MaxParsed);

        BlindSizeFormat.TryParse("1025 TB", out _, out var tooBig).Should().BeFalse();
        tooBig.Should().Contain("more than a device can hold");
    }
}
