using System.Security.Cryptography;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// BMB-304 fix round, finding 5: <c>bmb snapshot create</c> writes the archive under a temp name and gives it its name only when it is whole. A full volume
/// or a cancellation while it is written leaves no half-written <c>*.tar.gz</c> (nothing can list or restore one) and no temp file, and the command says it
/// failed; a snapshot that was made is a single complete archive.
/// </summary>
public sealed class SnapshotCommandArchiveCleanupTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bmb_cliarchive_" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryVaultKeySource _keys = new();
    private readonly byte[] _masterDek = RandomNumberGenerator.GetBytes(32);
    private readonly DbConnectionFactory _vault;

    public SnapshotCommandArchiveCleanupTests()
    {
        Directory.CreateDirectory(_data);
        _vault = new DbConnectionFactory(_data, _keys);
        using var conn = (SqliteConnection)_vault.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT); INSERT INTO tbl_article (title) VALUES ('note');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _vault.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_data, recursive: true); } catch { /* best-effort */ }
    }

    private string SnapshotsDir => Path.Combine(_data, "snapshots");

    private List<string> SnapshotFolderEntries() =>
        Directory.Exists(SnapshotsDir) ? Directory.EnumerateFileSystemEntries(SnapshotsDir).Select(Path.GetFileName).ToList()! : [];

    private List<string> StagingEntries() =>
        Directory.Exists(SnapshotStaging.DirIn(_data)) ? Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(_data)).ToList() : [];

    /// <summary>A stream that fails its first write the way a full volume does (and says how many bytes it was asked to take).</summary>
    private sealed class FullVolume(Stream inner, Action? onFirstWrite = null) : Stream
    {
        private bool _first = true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Fail()
        {
            if (!_first) return;
            _first = false;
            if (onFirstWrite is null) throw new IOException("There is not enough space on the disk.");
            onFirstWrite();
        }

        public override void Write(byte[] buffer, int offset, int count) { Fail(); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Fail(); inner.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Fail(); return inner.WriteAsync(buffer, cancellationToken); }
        public override async Task FlushAsync(CancellationToken cancellationToken) => await inner.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }

    [Fact]
    public async Task A_full_volume_while_the_archive_is_written_leaves_no_archive_and_no_temp_file()
    {
        var act = () => SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node", wrapArchive: s => new FullVolume(s));

        await act.Should().ThrowAsync<IOException>().WithMessage("*space*");

        SnapshotFolderEntries().Should().BeEmpty("no *.tar.gz, no temp archive: nothing that looks like a snapshot or holds half of one");
        StagingEntries().Should().BeEmpty("the working copy went too");
    }

    [Fact]
    public async Task A_cancellation_while_the_archive_is_written_leaves_no_archive_and_no_temp_file()
    {
        using var cts = new CancellationTokenSource();

        var act = () => SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node", cts.Token,
            wrapArchive: s => new FullVolume(s, onFirstWrite: cts.Cancel));

        await act.Should().ThrowAsync<OperationCanceledException>();

        SnapshotFolderEntries().Should().BeEmpty();
        StagingEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_snapshot_that_is_made_is_one_complete_archive_with_no_temp_file_beside_it_and_the_next_one_is_made_after_a_failure()
    {
        var failing = () => SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node", wrapArchive: s => new FullVolume(s));
        await failing.Should().ThrowAsync<IOException>();

        var result = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node");

        SnapshotFolderEntries().Should().ContainSingle().Which.Should().EndWith(".tar.gz");
        new FileInfo(result.Path).Length.Should().BeGreaterThan(0);
        File.Exists(result.Path).Should().BeTrue();
    }
}
