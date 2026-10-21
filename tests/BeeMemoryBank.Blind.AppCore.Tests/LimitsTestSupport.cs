using System.Net;
using System.Net.Http.Headers;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>A clock a test sets: UTC, and the device's local time zone (changeable, like a traveller's or a clock that is set back).</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;
    private TimeZoneInfo _zone;

    public FakeClock(DateTimeOffset start, TimeZoneInfo? zone = null)
    {
        _now = start;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => _zone;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;

    public void SetZone(TimeZoneInfo zone) => _zone = zone;

    /// <summary>Moves the clock to the first minute of the next local day.</summary>
    public void NextDay() => _now = BlindDayMath.StartOf(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_now, _zone).DateTime).AddDays(1), _zone).AddMinutes(1);

    public DateOnly LocalDate => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_now, _zone).DateTime);
}

/// <summary>Time zones built by hand so that the tests do not depend on the machine's tzdata: a daylight-saving zone and one that skips its midnight.</summary>
internal static class TestZones
{
    /// <summary>UTC+1; in 2026 clocks go forward at 02:00 on 29 March (a 23-hour day) and back at 03:00 on 25 October (a 25-hour day).</summary>
    public static readonly TimeZoneInfo Dst = TimeZoneInfo.CreateCustomTimeZone("Test/Dst", TimeSpan.FromHours(1), "Test DST", "Test DST", "Test DST summer",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25)),
    ]);

    /// <summary>UTC+0; on 29 March 2026 clocks go from 00:00 straight to 01:00: that day has no midnight.</summary>
    public static readonly TimeZoneInfo SkipsMidnight = TimeZoneInfo.CreateCustomTimeZone("Test/NoMidnight", TimeSpan.Zero, "Test no midnight", "Test no midnight", "Test no midnight summer",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25)),
    ]);

    /// <summary>UTC-5 all year (a zone far from UTC: the local date differs from the UTC date for part of the day).</summary>
    public static readonly TimeZoneInfo Minus5 = TimeZoneInfo.CreateCustomTimeZone("Test/Minus5", TimeSpan.FromHours(-5), "Test -5", "Test -5");
}

/// <summary>
/// A node that serves one file of <see cref="Length"/> bytes with byte ranges, generating the bytes as the client reads them (nothing is held in memory): the stand-in for a first load
/// that is far bigger than a day's allowance. It logs each request's Range header and counts the body bytes that were really READ from it.
/// </summary>
internal sealed class RangeServer(long length) : HttpMessageHandler
{
    private long _served;

    public long Length { get; } = length;

    /// <summary>Body bytes read by clients so far.</summary>
    public long BytesServed => Interlocked.Read(ref _served);

    public List<string?> RangeHeaders { get; } = [];

    public int Requests { get { lock (RangeHeaders) return RangeHeaders.Count; } }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        long from = 0;
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        lock (RangeHeaders) RangeHeaders.Add(request.Headers.Range?.ToString());
        if (range?.From is { } start) from = start;

        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(new SyntheticStream(from, Length - from, n => Interlocked.Add(ref _served, n))),
        };
        response.Content.Headers.ContentLength = Length - from;
        if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Length - 1, Length);
        return Task.FromResult(response);
    }

    /// <summary>The bytes of the file from <paramref name="start"/> on: each byte is the low byte of its position.</summary>
    private sealed class SyntheticStream(long start, long count, Action<long> served) : Stream
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
            for (var i = 0; i < n; i++) buffer[i] = (byte)((start + _position + i) & 0xFF);
            _position += n;
            if (n > 0) served(n);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A first load that downloads <see cref="RangeServer"/>'s file through the account's own HTTP client (so every byte is metered and guarded), keeping a partial file in the replica work
/// folder and resuming it by byte range: what <c>BlindPhoneReplicaClient</c> does, without the package format. A pause (<see cref="BlindPausedException"/>) is not caught here,
/// so the partial stays, as it does in the real client.
/// </summary>
internal sealed class RangeDownloadSource(BlindHttpClientProvider http, string url) : IBlindReplicaSource
{
    public async Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(workDirectory);
        var part = Path.Combine(workDirectory, "replica.part");
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var client = http.GetClient(target);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None);
        var buffer = new byte[80 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}

/// <summary>A pull client that does nothing but remember what it was asked (the sync round of an account is not what these tests are about).</summary>
internal sealed class RecordingPullClient : IBlindPhonePullClient
{
    public int Rounds;
    public List<BlindPause> Reports { get; } = [];
    public Exception? RoundFailure { get; set; }
    public Exception? ReportFailure { get; set; }

    public Task SyncOnceAsync(HttpClient http, BlindCallCode target, CancellationToken ct)
    {
        Interlocked.Increment(ref Rounds);
        return RoundFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task ReportPausedAsync(HttpClient http, BlindCallCode target, BlindPause pause, CancellationToken ct)
    {
        Reports.Add(pause);
        return ReportFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}

internal static class LimitsHostExtensions
{
    /// <summary>The account's entry as the registry holds it now.</summary>
    public static BlindAccountEntry EntryOf(this BlindAccountHost host, string id) => host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry;

    /// <summary>Pairs account <paramref name="id"/> with <paramref name="hub"/> through the real call-code route, and marks its first load done.</summary>
    public static async Task PairAsync(this BlindAccountHost host, AccountRig rig, string id, FakeBlindHub hub, bool loaded = true)
    {
        var secret = rig.Secrets.For(host.EntryOf(id)).LoadPairingSecret() ?? throw new InvalidOperationException("the account awaits no answer");
        var error = host.AcceptCallCode(hub.CallCodeFor(secret));
        if (error is not null) throw new InvalidOperationException(error);
        if (loaded) await host.SeedHubAsync(id, hub);
    }

    public static IServiceCollection ReplacePullClient(this IServiceCollection services, IBlindPhonePullClient client)
    {
        services.AddSingleton<IBlindPhonePullClient>(client);
        return services;
    }
}

/// <summary>Takes the right to list a folder away from this process, for the tests of what the app does with data it cannot read.</summary>
internal static class TestAccess
{
    /// <summary>
    /// Denies listing <paramref name="folder"/> (an ACL deny on Windows, mode 000 elsewhere) and returns what gives it back; null when this machine cannot make the folder unreadable (a process that
    /// is allowed anyway, such as root), in which case nothing was changed and the caller returns early.
    /// </summary>
    public static IDisposable? DenyListing(string folder)
    {
        Action restore;
        if (OperatingSystem.IsWindows())
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            if (Icacls($"\"{folder}\" /deny \"{user}:(RD)\"") != 0) return null;
            restore = () => Icacls($"\"{folder}\" /remove:d \"{user}\"");
        }
        else
        {
            var before = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.None);
            restore = () => File.SetUnixFileMode(folder, before);
        }

        if (CanList(folder))
        {
            restore();
            return null;
        }
        return new Restorer(restore);
    }

    private static bool CanList(string folder)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(folder);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static int Icacls(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", arguments)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return process.ExitCode;
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
