using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The Android blind node's backup file (plan section 10): open recovery-set header, body encrypted
/// with the backup key, chunked so an interrupted backup resumes. Everything that could make a changed
/// or cut file open as something else must fail instead.
/// </summary>
public sealed class AndroidBackupFileTests : IDisposable
{
    private const int Chunk = 1024;
    private const string RecoverySet = "{\"format\":\"bmb-recovery-set-v1\",\"boxes\":[],\"links\":[],\"anchors\":[],\"sealed_secrets\":[],\"created_at\":\"2026-09-27T12:00:00Z\"}";
    private static readonly byte[] Marker = "PLAINTEXT-METADATA-MARKER"u8.ToArray();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_abk_" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Guid _nodeId = Guid.NewGuid();

    public AndroidBackupFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk)]
    [InlineData(Chunk * 2 + 300)]
    public async Task RoundTrips_AndTheHeaderIsReadableWithoutTheKey(int size)
    {
        var source = Source(size);
        var output = await BackupAsync(source);

        await using var file = File.OpenRead(output);
        var header = await AndroidBackupFile.ReadHeaderAsync(file);
        header.NodeId.Should().Be(_nodeId);
        header.BackupKeyName.Should().Be($"android-backup:{_nodeId}");
        header.RecoverySet["format"]!.GetValue<string>().Should().Be("bmb-recovery-set-v1",
            "a restore reads the recovery set before it has any key");

        (await DecryptAsync(output, _key)).Should().Equal(File.ReadAllBytes(source));
        File.Exists(AndroidBackupWriter.PartPath(output)).Should().BeFalse();
        File.Exists(AndroidBackupWriter.StatePath(output)).Should().BeFalse();
    }

    [Fact]
    public async Task TheBodyIsEncrypted()
    {
        var output = await BackupAsync(Source(Chunk * 3));
        var bytes = File.ReadAllBytes(output);
        bytes.AsSpan().IndexOf(Marker).Should().Be(-1, "the network's metadata must not sit in the clear in a file that may go to the cloud");
    }

    [Fact]
    public async Task AWrongKey_DoesNotOpen()
    {
        var output = await BackupAsync(Source(Chunk * 2));
        await FluentActions.Awaiting(() => DecryptAsync(output, RandomNumberGenerator.GetBytes(32)))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task AChangedHeader_DoesNotOpen()
    {
        var output = await BackupAsync(Source(Chunk * 2));
        var bytes = File.ReadAllBytes(output);
        var at = bytes.AsSpan().IndexOf("2026-09-27T12:00:00Z"u8);
        bytes[at + 3] = (byte)'7'; // 2027: same length, still valid JSON, different header
        File.WriteAllBytes(output, bytes);

        await FluentActions.Awaiting(() => DecryptAsync(output, _key)).Should().ThrowAsync<InvalidDataException>(
            "the header is bound to every chunk, so a replaced recovery set cannot ride on a genuine body");
    }

    [Fact]
    public async Task ACutFile_DoesNotOpen()
    {
        var output = await BackupAsync(Source(Chunk * 3));
        var bytes = File.ReadAllBytes(output);
        File.WriteAllBytes(output, bytes[..^(Chunk + 16 + 4)]); // drop the final chunk

        var act = () => DecryptAsync(output, _key);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*cut short*");
    }

    [Theory]
    [InlineData(1)]      // inside the last chunk
    [InlineData(1100)]   // inside an earlier chunk
    public async Task AFileCutInsideAChunk_IsDamaged_NotAnIoError(int cut)
    {
        var output = await BackupAsync(Source(Chunk * 3));
        var bytes = File.ReadAllBytes(output);
        File.WriteAllBytes(output, bytes[..^cut]);

        await FluentActions.Awaiting(() => DecryptAsync(output, _key)).Should().ThrowAsync<InvalidDataException>(
            "a cut file is a damaged backup, reported as such");
    }

    [Fact]
    public async Task SwappedChunks_DoNotOpen()
    {
        var output = await BackupAsync(Source(Chunk * 3));
        var bytes = File.ReadAllBytes(output);
        var record = Chunk + 16 + 4;
        var firstChunk = bytes.Length - 3 * record;
        var a = bytes[firstChunk..(firstChunk + record)];
        var b = bytes[(firstChunk + record)..(firstChunk + 2 * record)];
        b.CopyTo(bytes, firstChunk);
        a.CopyTo(bytes, firstChunk + record);
        File.WriteAllBytes(output, bytes);

        await FluentActions.Awaiting(() => DecryptAsync(output, _key)).Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>
    /// A body larger than the caller takes is refused as it streams: no more than the limit (plus the chunk that
    /// crosses it, which is never written) reaches the output.
    /// </summary>
    [Fact]
    public async Task ABodyOverTheLimit_IsRefusedWhileStreaming()
    {
        var output = await BackupAsync(Source(Chunk * 5));
        await using var file = File.OpenRead(output);
        using var plain = new MemoryStream();

        var open = () => AndroidBackupFile.DecryptAsync(file, _key, plain, maxPlaintextBytes: Chunk * 2);

        (await open.Should().ThrowAsync<InvalidDataException>()).WithMessage("*larger than a restore takes*");
        plain.Length.Should().BeLessThanOrEqualTo(Chunk * 2);
    }

    [Fact]
    public async Task ABodyAtTheLimit_Opens()
    {
        var source = Source(Chunk * 2);
        var output = await BackupAsync(source);
        await using var file = File.OpenRead(output);
        using var plain = new MemoryStream();

        await AndroidBackupFile.DecryptAsync(file, _key, plain, maxPlaintextBytes: Chunk * 2);

        plain.ToArray().Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task TrailingData_DoesNotOpen()
    {
        var output = await BackupAsync(Source(Chunk));
        File.AppendAllText(output, "extra");
        await FluentActions.Awaiting(() => DecryptAsync(output, _key)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task AnInterruptedBackup_ResumesAfterItsLastWholeChunk()
    {
        var source = Source(Chunk * 5 + 17);
        var output = Path.Combine(_dir, "backup.bmbbackup");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p >= 0.4) cts.Cancel(); });

        await FluentActions.Awaiting(() => AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId,
                $"android-backup:{_nodeId}", RecoverySet, Chunk, progress, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        File.Exists(AndroidBackupWriter.PartPath(output)).Should().BeTrue();
        // Bytes past the last recorded chunk (a kill mid-write) must not survive into the resumed
        // file — more of them than the rest of the backup, so overwriting alone would not hide them.
        File.AppendAllBytes(AndroidBackupWriter.PartPath(output), new byte[Chunk * 10]);
        DateTimeOffset firstStart;
        await using (var part = File.OpenRead(AndroidBackupWriter.PartPath(output)))
            firstStart = (await AndroidBackupFile.ReadHeaderAsync(part)).CreatedAt;

        var reported = new List<double>();
        await AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId, $"android-backup:{_nodeId}", RecoverySet, Chunk,
            new SyncProgress(reported.Add));

        reported.First().Should().BeGreaterThan(0.4, "the second run continues, it does not start over");
        await using (var file = File.OpenRead(output))
            (await AndroidBackupFile.ReadHeaderAsync(file)).CreatedAt.Should().Be(firstStart);
        (await DecryptAsync(output, _key)).Should().Equal(File.ReadAllBytes(source));
    }

    // ─── Resume must trust nothing it has not checked ──────────────────────

    [Fact]
    public async Task Resume_AfterABitFlipInAnEarlierChunk_StartsOver_AndTheBackupOpens()
    {
        var (source, output) = await InterruptedAsync();
        var part = File.ReadAllBytes(AndroidBackupWriter.PartPath(output));
        part[FirstChunkOffset(part) + 4 + 10] ^= 0x01; // inside chunk 0's ciphertext
        File.WriteAllBytes(AndroidBackupWriter.PartPath(output), part);

        var first = await ResumeAsync(source, output, _key, RecoverySet);

        first.Should().BeLessThan(0.3, "a chunk that no longer opens means the file is not ours to continue");
        (await DecryptAsync(output, _key)).Should().Equal(File.ReadAllBytes(source));
    }

    /// <summary>
    /// Every field the resume actually reads from the state is checked against the partial file: an edited
    /// or stale value — a chunk count, an end offset, the preamble's length or digest — starts over.
    /// </summary>
    [Theory]
    [InlineData("chunks_done")]
    [InlineData("bytes_done")]
    [InlineData("preamble_length")]
    [InlineData("preamble_sha256")]
    public async Task Resume_WithAStaleState_StartsOver_AndTheBackupOpens(string field)
    {
        var (source, output) = await InterruptedAsync();
        var statePath = AndroidBackupWriter.StatePath(output);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        state[field] = field == "preamble_sha256"
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("some other header")))
            : state[field]!.GetValue<long>() + 1;
        File.WriteAllText(statePath, state.ToJsonString());

        var first = await ResumeAsync(source, output, _key, RecoverySet);

        first.Should().BeLessThan(0.3, "a state that does not describe the partial file is not trusted");
        (await DecryptAsync(output, _key)).Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Resume_WithAChangedRecoverySet_StartsOver_AndCarriesTheNewOne()
    {
        var (source, output) = await InterruptedAsync();
        var newer = RecoverySet.Replace("2026-09-27T12:00:00Z", "2026-09-28T08:00:00Z");

        var first = await ResumeAsync(source, output, _key, newer);

        first.Should().BeLessThan(0.3, "the header is bound to the chunks; a new header needs new chunks");
        await using var file = File.OpenRead(output);
        (await AndroidBackupFile.ReadHeaderAsync(file)).RecoverySet["created_at"]!.GetValue<string>()
            .Should().Be("2026-09-28T08:00:00Z");
        (await DecryptAsync(output, _key)).Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Resume_WithAnotherKey_StartsOver_AndOpensWithThatKey()
    {
        var (source, output) = await InterruptedAsync();
        var other = RandomNumberGenerator.GetBytes(32);

        var first = await ResumeAsync(source, output, other, RecoverySet);

        first.Should().BeLessThan(0.3, "chunks under one key cannot be continued under another");
        (await DecryptAsync(output, other)).Should().Equal(File.ReadAllBytes(source));
    }

    /// <summary>
    /// The source changes while it is being backed up, in a part not read yet. Sharing is not enforced
    /// on Android, so the writer does not rely on it: the finished file must hold exactly the source it
    /// started from, or it is discarded.
    /// </summary>
    [Fact]
    public async Task ASourceChangedWhileWriting_IsNotAcceptedAsABackup()
    {
        var source = Source(Chunk * 4);
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bmbbackup");
        var changed = false;
        var tamper = new SyncProgress(_ =>
        {
            if (changed) return;
            changed = true;
            using var f = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            f.Position = Chunk * 3 + 5; // a chunk not read yet
            f.WriteByte(0x42);
        });

        var write = () => AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId,
            $"android-backup:{_nodeId}", RecoverySet, Chunk, tamper);
        await write.Should().ThrowAsync<InvalidDataException>("the finished file must hold exactly the source it was started from");
        File.Exists(output).Should().BeFalse();
    }

    /// <summary>
    /// The harder case: a part ALREADY read is edited afterwards. The written file still matches the
    /// source as it was read, so only a fresh look at the live source can tell — and it must.
    /// </summary>
    [Fact]
    public async Task ASourceChangedInAPartAlreadyRead_IsNotAcceptedAsABackup()
    {
        var source = Source(Chunk * 4);
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bmbbackup");
        var tamper = new SyncProgress(p =>
        {
            if (p < 0.7 || p > 0.8) return; // after chunk 3 of 4
            using var f = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            f.Position = 5; // inside chunk 0, read long ago
            f.WriteByte(0x42);
        });

        var write = () => AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId,
            $"android-backup:{_nodeId}", RecoverySet, Chunk, tamper);

        await write.Should().ThrowAsync<InvalidDataException>(
            "a backup of a source that changed under it is not a backup of anything that ever existed");
        File.Exists(output).Should().BeFalse();
    }

    private async Task<(string Source, string Output)> InterruptedAsync()
    {
        var source = Source(Chunk * 5 + 17);
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bmbbackup");
        using var cts = new CancellationTokenSource();
        await FluentActions.Awaiting(() => AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId,
                $"android-backup:{_nodeId}", RecoverySet, Chunk, new SyncProgress(p => { if (p >= 0.4) cts.Cancel(); }), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        return (source, output);
    }

    /// <summary>Resumes and returns the first progress reported — small if it started over.</summary>
    private async Task<double> ResumeAsync(string source, string output, byte[] key, string recoverySet)
    {
        var reported = new List<double>();
        await AndroidBackupWriter.WriteAsync(source, output, key, _nodeId, $"android-backup:{_nodeId}", recoverySet, Chunk,
            new SyncProgress(reported.Add));
        return reported.First();
    }

    private static int FirstChunkOffset(byte[] file) =>
        8 + 4 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8, 4));

    [Fact]
    public async Task AChangedSource_StartsOver_InsteadOfReusingNonces()
    {
        var source = Source(Chunk * 4);
        var output = Path.Combine(_dir, "backup.bmbbackup");
        using var cts = new CancellationTokenSource();
        await FluentActions.Awaiting(() => AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId,
                $"android-backup:{_nodeId}", RecoverySet, Chunk, new SyncProgress(_ => cts.Cancel()), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        string firstPrefix;
        await using (var part = File.OpenRead(AndroidBackupWriter.PartPath(output)))
            firstPrefix = (await AndroidBackupFile.ReadHeaderAsync(part)).NoncePrefix;

        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(Chunk * 4)); // same length, new content
        var reported = new List<double>();
        await AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId, $"android-backup:{_nodeId}", RecoverySet, Chunk,
            new SyncProgress(reported.Add));

        reported.First().Should().BeLessThan(0.3, "a different source must not continue the old file");
        await using (var file = File.OpenRead(output))
            (await AndroidBackupFile.ReadHeaderAsync(file)).NoncePrefix.Should().NotBe(firstPrefix,
                "new plaintext under the old nonces would break AES-GCM");
        (await DecryptAsync(output, _key)).Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task NotABackup_IsRefusedWithoutReadingFurther()
    {
        var path = Path.Combine(_dir, "junk.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello, not a backup at all"));
        await using var file = File.OpenRead(path);
        await FluentActions.Awaiting(() => AndroidBackupFile.ReadHeaderAsync(file)).Should().ThrowAsync<InvalidDataException>();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string Source(int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        if (size >= Marker.Length) Marker.CopyTo(bytes, size / 2 - Math.Min(size / 2, Marker.Length / 2));
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".src");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<string> BackupAsync(string source)
    {
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bmbbackup");
        await AndroidBackupWriter.WriteAsync(source, output, _key, _nodeId, $"android-backup:{_nodeId}", RecoverySet, Chunk);
        return output;
    }

    private static async Task<byte[]> DecryptAsync(string path, byte[] key)
    {
        await using var file = File.OpenRead(path);
        using var plain = new MemoryStream();
        await AndroidBackupFile.DecryptAsync(file, key, plain);
        return plain.ToArray();
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; the tests need the report inline.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
