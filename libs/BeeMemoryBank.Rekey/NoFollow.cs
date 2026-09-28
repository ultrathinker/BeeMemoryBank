namespace BeeMemoryBank.Rekey;

/// <summary>
/// The re-key never follows a junction or a symbolic link out of the directories it works on (review release-b R1-7):
/// a link under D could otherwise import files from anywhere into the new vault, make it read an outside file as a
/// vault's media, or loop a walk forever. Links are skipped (and named, so the report can say so), never followed.
/// </summary>
public static class NoFollow
{
    /// <summary>Whether <paramref name="path"/> is a junction, a symbolic link or another reparse point.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    /// <summary>A recursive walk that does not enter links (they are not returned either).</summary>
    public static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
    };

    /// <summary>
    /// Deletes a directory tree the re-key created, removing any link inside it as a link. A <paramref name="dir"/>
    /// that is itself a link goes as a link: its target is never touched.
    /// </summary>
    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir) && !File.Exists(dir)) return;
        if (IsLink(dir))
        {
            Directory.Delete(dir);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (IsLink(entry))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry); else File.Delete(entry);
            }
            else if (Directory.Exists(entry)) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(dir);
    }
}
