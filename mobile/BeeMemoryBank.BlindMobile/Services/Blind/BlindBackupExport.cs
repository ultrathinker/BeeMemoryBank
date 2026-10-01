using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The part of "Save to…" that is not Android: what goes into the stream the system file picker (Storage Access
/// Framework) hands back. Only a finished backup file is saved — never a half-written one or a stranger — and the
/// result is checked: a provider that accepts the bytes and keeps fewer of them must show up as a failure,
/// because the user is told "saved" only when the file in the chosen place is the whole backup.
/// </summary>
public static class BlindBackupExport
{
    private const int BufferSize = 256 * 1024;

    /// <returns>The number of bytes saved.</returns>
    /// <exception cref="InvalidDataException">The file is not a backup of this app.</exception>
    /// <exception cref="IOException">The place kept fewer bytes than it was given.</exception>
    public static async Task<long> CopyAsync(string backupPath, Stream target, IProgress<double>? progress, CancellationToken ct)
    {
        if (!AndroidBackupRestore.LooksLikeBackup(backupPath))
            throw new InvalidDataException("This is not a finished backup file; nothing was saved.");

        await using var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
        var total = source.Length;
        var buffer = new byte[BufferSize];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            progress?.Report(total == 0 ? 1 : (double)copied / total);
        }
        await target.FlushAsync(ct);

        if (copied != total || (target.CanSeek && target.Length != total))
            throw new IOException("The chosen place kept an incomplete file; the backup was not saved.");
        return copied;
    }
}
