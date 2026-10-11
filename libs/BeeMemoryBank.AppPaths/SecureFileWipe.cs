using System.Security.Cryptography;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// Best-effort removal of a file that holds plain vault data (docs/design/step-b-spec.md 10.6): overwrite in place with random
/// bytes, flush to disk, truncate, delete. SSDs with wear levelling and copy-on-write or journaling file systems may keep earlier
/// blocks; that remnant is documented (spec 2.1), this only removes the file and its readable bytes. A file that has another name
/// is never overwritten (spec 10.2): its bytes belong to that name too.
/// </summary>
public static class SecureFileWipe
{
    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// Wipes <paramref name="path"/>. Returns true when the file is gone (or never existed), false when it could not be removed
    /// (locked, read-only volume, access denied): the caller keeps its journal and retries at the next start. A symbolic link or
    /// reparse point is removed without ever writing through it.
    /// </summary>
    /// <param name="afterOverwrite">Test seam: called after the content is overwritten and flushed, before the file is truncated.</param>
    /// <param name="sharedWithAnotherName">
    /// Asked once for a regular file before anything is written: true when the file has another name (a link count above one, or the
    /// identity of a live file). The file is then NOT overwritten - only this name is removed and true is returned; the bytes live on
    /// under the other name, which is the caller's to report. An exception thrown by the callback propagates (the caller decides what an
    /// unreadable identity means). Null: the file is overwritten.
    /// </param>
    public static bool TryWipe(string path, Action<string>? afterOverwrite = null, Func<string, bool>? sharedWithAnotherName = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                info.Delete();
                return true;
            }
            if (!info.Exists) return true;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete();
                return true;
            }
            if (sharedWithAnotherName?.Invoke(path) == true)
            {
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                return true;
            }
            if (info.IsReadOnly) info.IsReadOnly = false;

            // FileShare.Read: no other writer, but the test seam below can read the overwritten bytes.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
            {
                var length = stream.Length;
                var block = new byte[BlockSize];
                for (long written = 0; written < length; written += block.Length)
                {
                    RandomNumberGenerator.Fill(block);
                    stream.Write(block, 0, (int)Math.Min(block.Length, length - written));
                }
                stream.Flush(true);
                afterOverwrite?.Invoke(path);
                stream.SetLength(0);
                stream.Flush(true);
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
