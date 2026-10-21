using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The stored-size cap and what a peer stages on the node (srvfix6, R4-04): the seed's staging and the other areas the node writes into for a peer are part of what the cap
/// measures, and a seed that would fill the disk beyond it is refused as a pause while it is still arriving.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindStagingCapTests : IDisposable
{
    private const long MiB = 1L << 20;

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));
    private readonly BlindNodeFactory _node = new();
    private readonly Guid _seeder = Guid.NewGuid();

    public BlindStagingCapTests() => Directory.CreateDirectory(_dataPath);

    public void Dispose()
    {
        _node.Dispose();
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    private static void Fill(string path, long bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        file.SetLength(bytes);
    }

    // ── the measure ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("blind-tmp/seed-0123456789abcdef/package.tar.gz")]
    [InlineData("blind-tmp/seed-0123456789abcdef/package/media/a.enc")]
    [InlineData("blind-tmp/packages/replica.tar.gz")]
    [InlineData("blind-tmp/staging/part")]
    [InlineData("blind-cutover/new.db")]
    [InlineData("blind-cutover/media/b.enc")]
    [InlineData("media/c.enc")]
    [InlineData("baselines/current/d.bin")]
    public void The_stored_size_counts_every_area_the_node_writes_into_for_a_peer(string relative)
    {
        Fill(Path.Combine(_dataPath, relative.Replace('/', Path.DirectorySeparatorChar)), 3 * MiB);
        var size = new BlindStoredSize(_dataPath, TimeProvider.System);

        size.EnsureFresh();

        size.Peek().Should().Be(new BlindStoredReading(true, 3 * MiB), relative);
    }

    // ── a seed that arrives in parts ──────────────────────────────────────

    private async Task<(HttpClient Http, string Secret)> NodeWithCapAsync(long roomBytes)
    {
        _ = _node.Services;
        var http = _node.Server.CreateClient();
        using var local = _node.CreateClient();
        var code = BlindPairCode.Parse((await local.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!);
        var guard = _node.Services.GetRequiredService<BlindUsageGuard>();
        var held = guard.StoredNow().Bytes;
        _node.Services.GetRequiredService<BlindBackupSettingsStore>().Update(s =>
        {
            s.MaxStoredBytes = held + roomBytes;
            return true;
        }).Should().BeTrue();
        return (http, code.Secret);
    }

    private HttpRequestMessage SeedPart(string secret, Guid seedId, long offset, long total, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/blind/seed?seedId={seedId}&offset={offset}&total={total}&sha256={new string('0', 64)}") { Content = content };
        var key = Convert.ToBase64String(new byte[32]);
        request.Headers.Add(BlindSeederProof.NodeIdHeader, _seeder.ToString());
        request.Headers.Add(BlindSeederProof.KeyHeader, key);
        request.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(secret, seedId, _seeder, new byte[32]));
        return request;
    }

    private string[] StagingDirs() => Directory.Exists(Path.Combine(_node.DataPath, "blind-tmp"))
        ? Directory.GetDirectories(Path.Combine(_node.DataPath, "blind-tmp"), "seed-*")
        : [];

    [Fact]
    public async Task R4_04_a_chunked_upload_that_would_fill_the_disk_beyond_the_cap_is_refused_as_a_pause_while_it_arrives()
    {
        var (http, secret) = await NodeWithCapAsync(roomBytes: 2 * MiB);
        var seedId = Guid.NewGuid();
        // No Content-Length: the planned size the door sees is zero.
        using var content = new StreamContent(new ZeroStream(8 * MiB));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = SeedPart(secret, seedId, 0, 64 * MiB, content);
        request.Headers.TransferEncodingChunked = true;

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "8 MiB of staging on a node with 2 MiB of room");
        response.Headers.GetValues(PeerPausedException.HeaderName).Should().ContainSingle().Which.Should().Be("disk");
        response.Headers.RetryAfter.Should().NotBeNull();
        StagingDirs().Should().BeEmpty("the node's own cleanup removes what a refused seed staged");
    }

    // (R4_04_parts_that_each_fit_are_refused_when_together_with_the_node_they_reach_the_cap pinned a seed of 64 MiB being staged 8 MiB at a time on 10 MiB of room: the announced
    // total is judged at the first part now - see BlindSeedRound2Tests, RV5_03_* - and what the node grows by meanwhile is judged at every part.)

    [Fact]
    public async Task A_seed_that_fits_under_the_cap_is_still_received()
    {
        var (http, secret) = await NodeWithCapAsync(roomBytes: 50 * MiB);
        using var request = SeedPart(secret, Guid.NewGuid(), 0, 40 * MiB, new ByteArrayContent(new byte[4 * MiB]));

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        StagingDirs().Should().ContainSingle();
    }

    [Fact]
    public async Task R4_04_a_wipe_removes_what_a_seed_staged_so_that_a_wiped_node_is_not_held_by_it()
    {
        var (http, secret) = await NodeWithCapAsync(roomBytes: 50 * MiB);
        using (var first = SeedPart(secret, Guid.NewGuid(), 0, 40 * MiB, new ByteArrayContent(new byte[4 * MiB])))
            (await http.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        StagingDirs().Should().ContainSingle();
        using var local = _node.CreateClient();
        (await local.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" })).EnsureSuccessStatusCode();
        var name = (await local.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/blind/status")).GetProperty("node_name").GetString();

        var wipe = await local.PostAsJsonAsync("/api/blind/wipe", new { consolePassword = "console-pw-123", confirmNodeName = name });

        wipe.StatusCode.Should().Be(HttpStatusCode.OK, await wipe.Content.ReadAsStringAsync());
        StagingDirs().Should().BeEmpty("the staging of the seed that was arriving is gone with everything else");
        var code = BlindPairCode.Parse((await local.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!);
        using var next = SeedPart(code.Secret, Guid.NewGuid(), 0, 64 * MiB, new ByteArrayContent(new byte[MiB]));
        (await http.SendAsync(next)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the abandoned upload does not hold the wiped node for an hour");
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Take(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Take(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Take(buffer.AsSpan(offset, count)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, _left);
            buffer[..n].Clear();
            _left -= n;
            return n;
        }
    }
}
