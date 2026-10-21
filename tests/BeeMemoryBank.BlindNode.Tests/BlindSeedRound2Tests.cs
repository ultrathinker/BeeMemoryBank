using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The second round on a seed that arrives under the stored cap (srvfix7, RV5-03, RV5-04, RV5-07, RV5-08): the announced total is judged at the first part, what a seed stages
/// and applies does not pause the node's other callers, an upload nobody continues is dropped, and a wipe stops a seed that is being received.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindSeedRound2Tests : IDisposable
{
    private const long MiB = 1L << 20;

    private readonly SteppedClock _clock = new(DateTimeOffset.UtcNow);
    private readonly ClockedNodeFactory _node;
    private readonly Guid _seeder = Guid.NewGuid();

    public BlindSeedRound2Tests() => _node = new ClockedNodeFactory(_clock);

    public void Dispose() => _node.Dispose();

    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        private long _shift;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _shift, by.Ticks);

        public override DateTimeOffset GetUtcNow() => now.AddTicks(Interlocked.Read(ref _shift)) + (DateTimeOffset.UtcNow - now);
    }

    private sealed class ClockedNodeFactory(TimeProvider clock) : BlindNodeFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        }
    }

    private async Task<(HttpClient Http, string Secret, long Held)> NodeWithCapAsync(long roomBytes)
    {
        _ = _node.Services;
        var http = _node.Server.CreateClient();
        using var local = _node.CreateClient();
        var code = BlindPairCode.Parse((await local.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!);
        var held = _node.Services.GetRequiredService<BlindUsageGuard>().StoredNow().Bytes;
        SetCap(held + roomBytes);
        return (http, code.Secret, held);
    }

    private void SetCap(long bytes) => _node.Services.GetRequiredService<BlindBackupSettingsStore>().Update(s =>
    {
        s.MaxStoredBytes = bytes;
        return true;
    }).Should().BeTrue();

    private HttpRequestMessage SeedPart(string secret, Guid seedId, long offset, long total, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/blind/seed?seedId={seedId}&offset={offset}&total={total}&sha256={new string('0', 64)}") { Content = content };
        request.Headers.Add(BlindSeederProof.NodeIdHeader, _seeder.ToString());
        request.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(new byte[32]));
        request.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(secret, seedId, _seeder, new byte[32]));
        return request;
    }

    private string[] StagingDirs() => Directory.Exists(Path.Combine(_node.DataPath, "blind-tmp"))
        ? Directory.GetDirectories(Path.Combine(_node.DataPath, "blind-tmp"), "seed-*")
        : [];

    // ── RV5-03: the announced total is judged at the first part ──

    [Fact]
    public async Task RV5_03_a_seed_whose_announced_total_cannot_fit_is_refused_before_a_byte_is_staged()
    {
        var (http, secret, _) = await NodeWithCapAsync(roomBytes: 10 * MiB);
        using var request = SeedPart(secret, Guid.NewGuid(), 0, 64 * MiB, new ByteArrayContent(new byte[4 * MiB]));

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "64 MiB cannot be staged on a node with 10 MiB of room: no part of it is worth sending");
        response.Headers.GetValues(PeerPausedException.HeaderName).Should().ContainSingle().Which.Should().Be("disk");
        StagingDirs().Should().BeEmpty("not a byte was staged");
    }

    [Fact]
    public async Task A_seed_whose_total_fits_is_received_part_by_part()
    {
        var (http, secret, _) = await NodeWithCapAsync(roomBytes: 10 * MiB);
        var seedId = Guid.NewGuid();
        var statuses = new List<HttpStatusCode>();

        for (var part = 0; part < 2; part++)
        {
            using var request = SeedPart(secret, seedId, part * 4 * MiB, 9 * MiB, new ByteArrayContent(new byte[4 * MiB]));
            using var response = await http.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Accepted, HttpStatusCode.Accepted);
        StagingDirs().Should().ContainSingle();
    }

    [Fact]
    public async Task What_the_node_grows_by_meanwhile_is_judged_at_the_next_part_and_the_staging_goes()
    {
        var (http, secret, _) = await NodeWithCapAsync(roomBytes: 10 * MiB);
        var seedId = Guid.NewGuid();
        using (var first = SeedPart(secret, seedId, 0, 9 * MiB, new ByteArrayContent(new byte[4 * MiB])))
            (await http.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        // Phones push media meanwhile: the node now holds 6 MiB more than it did.
        var media = Path.Combine(_node.DataPath, "media");
        Directory.CreateDirectory(media);
        using (var file = File.Create(Path.Combine(media, "pushed.enc"))) file.SetLength(6 * MiB);

        using var second = SeedPart(secret, seedId, 4 * MiB, 9 * MiB, new ByteArrayContent(new byte[4 * MiB]));
        using var response = await http.SendAsync(second);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        StagingDirs().Should().BeEmpty();
    }

    // ── RV5-07: the staging of an upload nobody continues does not pause the node's other callers ──

    [Fact]
    public async Task RV5_07_the_staging_of_a_seed_does_not_pause_the_phones_that_pull_from_the_node()
    {
        var (http, secret, held) = await NodeWithCapAsync(roomBytes: 50 * MiB);
        using (var part = SeedPart(secret, Guid.NewGuid(), 0, 40 * MiB, new ByteArrayContent(new byte[4 * MiB])))
            (await http.SendAsync(part)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        // The seed judged itself against the room it had; the owner then lowers the limit below what the node holds and has staged together, and a minute passes.
        SetCap(held + 3 * MiB);
        _clock.Advance(TimeSpan.FromMinutes(2));

        using var pull = await http.GetAsync("/api/sync/events?afterSequence=0&limit=10");

        pull.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the door answers for what the node holds; the staging of a seed is the seed's own affair (it judges itself part by part)");
    }

    // ── RV5-04: the apply's own peak is not the door's business ──

    [Fact]
    public void RV5_04_the_stored_size_leaves_out_what_a_seed_works_in_while_it_works_and_measures_again_after()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Fill(Path.Combine(dir, "blind-cutover", "new.db"), 5 * MiB);
            Fill(Path.Combine(dir, "blind-tmp", "seed-0123456789abcdef", "package.tar.gz"), 3 * MiB);
            var size = new BlindStoredSize(dir, TimeProvider.System);
            size.EnsureFresh();
            size.Peek().Bytes.Should().Be(8 * MiB);

            using (size.Exclude(Path.Combine(dir, "blind-cutover")))
            {
                size.EnsureFresh();
                size.Peek().Bytes.Should().Be(3 * MiB, "the new database a seed is building is not what the node holds yet");
                using (size.Exclude(Path.Combine(dir, "blind-tmp", "seed-0123456789abcdef")))
                {
                    size.EnsureFresh();
                    size.Peek().Bytes.Should().Be(0);
                }
            }

            size.EnsureFresh();
            size.Peek().Bytes.Should().Be(8 * MiB, "the registration ended with the apply: the cache was dropped, the next decision measures again");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static void Fill(string path, long bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        file.SetLength(bytes);
    }

    [Fact]
    public async Task RV5_04_the_door_does_not_see_the_peak_of_an_apply_and_sees_the_node_again_when_it_is_over()
    {
        _ = _node.Services;
        var guard = _node.Services.GetRequiredService<BlindUsageGuard>();
        var held = guard.StoredNow().Bytes;
        SetCap(held + 6 * MiB);
        var cutover = Path.Combine(_node.DataPath, "blind-cutover");
        Directory.CreateDirectory(cutover);
        using (var file = File.Create(Path.Combine(cutover, "new.db"))) file.SetLength(9 * MiB);
        var http = _node.Server.CreateClient();
        _clock.Advance(TimeSpan.FromMinutes(2));

        HttpStatusCode during;
        using (guard.ExcludeFromDoor(cutover))
        {
            using var pull = await http.GetAsync("/api/sync/events?afterSequence=0&limit=10");
            during = pull.StatusCode;
        }
        _clock.Advance(TimeSpan.FromMinutes(2));
        using var after = await http.GetAsync("/api/sync/events?afterSequence=0&limit=10");

        during.Should().Be(HttpStatusCode.Unauthorized, "while a seed applies, a phone's pull is answered as ever (here: 401, it has no token)");
        after.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "a cutover that is left behind is real disk and counts again once the apply is over");
    }

    // ── RV5-07: an upload nobody continues is dropped on its own ──

    [Fact]
    public async Task RV5_07_an_upload_nobody_continues_is_dropped_with_its_staging_after_an_hour_without_a_new_seed()
    {
        var (http, secret, _) = await NodeWithCapAsync(roomBytes: 50 * MiB);
        var seeds = _node.Services.GetRequiredService<BlindSeedService>();
        using (var part = SeedPart(secret, Guid.NewGuid(), 0, 40 * MiB, new ByteArrayContent(new byte[4 * MiB])))
            (await http.SendAsync(part)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        seeds.SweepAbandoned();
        StagingDirs().Should().ContainSingle("a sender that comes back within the hour finds its upload");

        _clock.Advance(TimeSpan.FromMinutes(61));
        seeds.SweepAbandoned();

        StagingDirs().Should().BeEmpty("the sender's computer slept for a week: the owner's disk is not held for it");
    }

    // ── RV5-08: the seed's barrier ──

    [Fact]
    public async Task RV5_08_a_seed_that_starts_while_the_wipe_holds_the_service_is_refused_and_one_after_it_runs()
    {
        _ = _node.Services;
        var seeds = _node.Services.GetRequiredService<BlindSeedService>();
        var authority = new BlindSeedAuthority.SuperadminPeer(Guid.NewGuid());

        using (await seeds.QuiesceAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
            await FluentActions.Awaiting(() => seeds.ReceiveAsync(Guid.NewGuid(), 0, 10, new string('0', 64), authority, new MemoryStream(new byte[10]), CancellationToken.None))
                .Should().ThrowAsync<BlindSeedWipingException>();

        // Open again: the first part of a 20-byte package is received (10 bytes of it), not refused.
        var progress = await seeds.ReceiveAsync(Guid.NewGuid(), 0, 20, new string('0', 64), authority, new MemoryStream(new byte[10]), CancellationToken.None, announcedBytes: 10);
        progress.Received.Should().Be(10);
        progress.Applied.Should().BeFalse();
    }

    [Fact]
    public async Task RV5_08_a_seed_that_does_not_leave_in_time_fails_the_barrier_and_reopens_the_service()
    {
        _ = _node.Services;
        var seeds = _node.Services.GetRequiredService<BlindSeedService>();
        using var first = await seeds.QuiesceAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await FluentActions.Awaiting(() => seeds.QuiesceAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None)).Should().ThrowAsync<TimeoutException>();
        first.Dispose();

        var progress = await seeds.ReceiveAsync(Guid.NewGuid(), 0, 20, new string('0', 64), new BlindSeedAuthority.SuperadminPeer(Guid.NewGuid()), new MemoryStream(new byte[10]), CancellationToken.None, announcedBytes: 10);
        progress.Received.Should().Be(10, "the service is open again: the part is received");
        progress.Applied.Should().BeFalse();
    }

    // ── RV5-08: a wipe stops a seed that is being received ──

    [Fact]
    public async Task RV5_08_a_seed_part_being_received_when_the_node_is_wiped_is_stopped_and_stages_nothing_after_the_wipe()
    {
        var (http, secret, _) = await NodeWithCapAsync(roomBytes: 50 * MiB);
        var stall = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        var content = new StreamContent(new StallingStream(MiB, stall.Task, received));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = SeedPart(secret, Guid.NewGuid(), 0, 40 * MiB, content);
        request.Headers.TransferEncodingChunked = true;
        var seeding = http.SendAsync(request);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The node is writing the part: its staging file has bytes in it.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !StagingDirs().Any(d => File.Exists(Path.Combine(d, "package.tar.gz")) && new FileInfo(Path.Combine(d, "package.tar.gz")).Length > 0))
            await Task.Delay(20);
        StagingDirs().Should().NotBeEmpty();
        using var local = _node.CreateClient();
        (await local.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" })).EnsureSuccessStatusCode();
        var name = (await local.GetFromJsonAsync<JsonElement>("/api/blind/status")).GetProperty("node_name").GetString();

        var wipe = await local.PostAsJsonAsync("/api/blind/wipe", new { consolePassword = "console-pw-123", confirmNodeName = name });
        stall.TrySetResult();
        HttpStatusCode? seedStatus = null;
        try
        {
            using var response = await seeding.WaitAsync(TimeSpan.FromSeconds(30));
            seedStatus = response.StatusCode;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // A part the wipe stopped may end in any way except an accepted one.
        }

        wipe.StatusCode.Should().Be(HttpStatusCode.OK, await wipe.Content.ReadAsStringAsync());
        seedStatus.Should().NotBe(HttpStatusCode.Accepted, "the seed was authorized before the wipe; the node it was for is gone");
        StagingDirs().Should().BeEmpty("a wiped node keeps nothing of a seed that was arriving");
    }

    private sealed class StallingStream(long first, Task stall, TaskCompletionSource received) : Stream
    {
        private long _left = first;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left > 0)
            {
                var n = (int)Math.Min(buffer.Length, _left);
                buffer.Span[..n].Clear();
                _left -= n;
                if (_left == 0) received.TrySetResult();
                return n;
            }
            await stall.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
