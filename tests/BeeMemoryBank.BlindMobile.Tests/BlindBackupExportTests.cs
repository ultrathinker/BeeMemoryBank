using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// "Save to…" (plan section 10): the file picker is Android's (<c>SafExport</c>, device only), what goes into the
/// stream it hands back is checked here. Only a finished backup file is saved, whole, and a stream that took
/// less than it was given is a failure the screen reports — never a "saved" over a cut-off file.
/// </summary>
public sealed class BlindBackupExportTests
{
    [Fact]
    public async Task AFinishedBackup_IsCopiedWhole_WithProgressUpToOne()
    {
        var (path, bytes) = await BackupFileAsync(bodyLength: 700_000);
        using var target = new MemoryStream();
        var fractions = new List<double>();

        var copied = await BlindBackupExport.CopyAsync(path, target, new InlineProgress(fractions.Add), CancellationToken.None);

        copied.Should().Be(bytes.Length);
        target.ToArray().Should().Equal(bytes);
        fractions.Should().NotBeEmpty();
        fractions[^1].Should().Be(1);
        fractions.Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData("not a backup at all")]
    [InlineData("")]
    public async Task ANonBackupFile_IsNotSaved_AndNothingIsWritten(string content)
    {
        var path = Path.Combine(NewDir(), "bmb-phone-1.bmbbackup");
        await File.WriteAllTextAsync(path, content);
        using var target = new MemoryStream();

        var act = () => BlindBackupExport.CopyAsync(path, target, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        target.Length.Should().Be(0, "the chosen place must not receive a file nobody can open");
    }

    [Fact]
    public async Task AStreamThatTookLessThanItWasGiven_IsAFailure_NotASave()
    {
        var (path, bytes) = await BackupFileAsync(bodyLength: 300_000);
        using var inner = new MemoryStream();
        using var target = new ForgetfulStream(inner, keepAtMost: bytes.Length - 10);

        var act = () => BlindBackupExport.CopyAsync(path, target, null, CancellationToken.None);

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("incomplete");
    }

    [Fact]
    public async Task ACancelledSave_ThrowsAndDoesNotClaimSuccess()
    {
        var (path, bytes) = await BackupFileAsync(bodyLength: 700_000);
        using var cts = new CancellationTokenSource();
        using var target = new MemoryStream();
        var progress = new InlineProgress(_ => cts.Cancel());

        var act = () => BlindBackupExport.CopyAsync(path, target, progress, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        target.Length.Should().BeLessThan(bytes.Length, "it stopped when asked, it did not finish the copy first");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-s4-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A real finished backup file, written by the same writer the phone uses.</summary>
    private static async Task<(string Path, byte[] Bytes)> BackupFileAsync(int bodyLength)
    {
        var dir = NewDir();
        var body = Path.Combine(dir, "body");
        await File.WriteAllBytesAsync(body, RandomNumberGenerator.GetBytes(bodyLength));
        var path = Path.Combine(dir, "bmb-phone-20260101-000000.bmbbackup");
        var nodeId = BlindNodeId.NewId();
        await AndroidBackupWriter.WriteAsync(body, path, RandomNumberGenerator.GetBytes(32), nodeId, $"android-backup:{nodeId}",
            """{"format":"bmb-recovery-set-v1","boxes":[],"links":[],"anchors":[],"sealed_secrets":[],"created_at":"t"}""");
        return (path, await File.ReadAllBytesAsync(path));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A stream that says it wrote everything and keeps only the first bytes (some providers do).</summary>
    private sealed class ForgetfulStream(MemoryStream inner, long keepAtMost) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = (int)Math.Max(0, Math.Min(count, keepAtMost - inner.Length));
            inner.Write(buffer, offset, room);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }
    }
}
