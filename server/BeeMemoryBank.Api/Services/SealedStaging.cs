using System.IO.Pipelines;
using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// A staging copy of the vault made from a stream of plain image bytes that must never be a file on an encrypted node (docs/design/step-b-spec.md 8.4):
/// a baseline's decrypted database, a join archive's. The producer of the stream (a container decrypting into the sink it is given) runs twice - once
/// into a counter, which authenticates the whole container and tells the importer how long the image is, and once into a pipe that the importer reads
/// into a file encrypted with the vault's page key - so nothing is imported from a container that does not open to its end, and no plain byte is stored.
/// </summary>
internal static class SealedStaging
{
    /// <param name="target">The vault's factory: the page key of the staging copy and the memory cap rule come from it.</param>
    /// <param name="stagingPath">An existing owner-only file (<c>SnapshotStaging.NewFile</c>) that receives the encrypted copy in place.</param>
    /// <param name="decryptInto">Writes the plain image to the stream it is given; called twice and must be repeatable.</param>
    public static async Task ImportAsync(DbConnectionFactory target, string stagingPath, Func<Stream, CancellationToken, Task> decryptInto, CancellationToken ct)
    {
        var counter = new CountingSink();
        await decryptInto(counter, ct);

        var sealing = stagingPath + ".sealing";
        var pageKey = target.CopyPageKey() ?? throw new InvalidOperationException("The target database is not encrypted.");
        try
        {
            var pipe = new Pipe();
            var import = Task.Run(async () =>
            {
                await using var reader = pipe.Reader.AsStream();
                await PlainImageImporter.ToSealedFileAsync(reader, counter.Length, sealing, pageKey, PlainImageImporter.DefaultMemoryCap(), ct);
            }, ct);
            try
            {
                await using (var writer = pipe.Writer.AsStream())
                    await decryptInto(writer, ct);
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex);
                try { await import; } catch { /* the cause is the producer's */ }
                throw;
            }
            await import;

            // In place, so the owner-only file that was made for it keeps its permissions and no rename has to win against an open handle.
            await using var source = new FileStream(sealing, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var destination = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            await source.CopyToAsync(destination, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pageKey);
            if (File.Exists(sealing)) File.Delete(sealing);
        }
    }

    private sealed class CountingSink : Stream
    {
        private long _count;
        public override long Length => _count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _count += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _count += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
