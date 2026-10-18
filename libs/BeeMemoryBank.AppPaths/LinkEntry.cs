namespace BeeMemoryBank.AppPaths;

/// <summary>
/// What a directory entry IS, decided from the entry itself and never from what it points to. <c>File.Exists</c>, <c>Directory.Exists</c> and <c>FileSystemInfo.Exists</c>
/// answer for what a link resolves to, and what they answer for a link to nothing depends on the platform, the kind of link and the runtime version (on .NET 10 a file link to
/// nothing still "exists", a folder link to nothing does not on Linux). A check for a link that starts with one of them would call such a link nothing, and the
/// code that follows would read it as "no marker, no journal, no file" or write through it and create its target wherever it points; this answers the same everywhere.
/// </summary>
internal static class LinkEntry
{
    /// <summary>
    /// True for a symbolic link, a junction or another reparse point, whether or not its target exists. An entry that is not there is no link; one whose own attributes cannot be
    /// read fails closed (a link).
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null) return true;
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True when a file, or a link of any kind (a dangling one too), is at <paramref name="path"/>: what a marker or a journal reads as "it is there".</summary>
    public static bool Exists(string path) => File.Exists(path) || IsLink(path);
}
