namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// Writes a file so that a reader (or a crash, or a power cut) sees either the old content or the new content, never half of
/// it: the bytes go to a temp file in the same folder, are flushed to disk, and the temp file then replaces the target.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // The temp file is the only thing this method creates; a failed write must not leave it behind to confuse a reader.
            TryRemove(temp);
            throw;
        }
    }

    private static void TryRemove(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
