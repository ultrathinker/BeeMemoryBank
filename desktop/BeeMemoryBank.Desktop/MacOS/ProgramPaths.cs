using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>Reads symbolic links. A seam: the walk below is tested on a fake file system on every OS, and on real links on a Mac.</summary>
internal interface ILinkReader
{
    /// <summary>The text a symbolic link at this absolute path points to (relative or absolute, as stored); null when it is not a link or does not exist.</summary>
    string? ReadLink(string absolutePath);
}

internal sealed class FileSystemLinkReader : ILinkReader
{
    public string? ReadLink(string absolutePath)
    {
        try { return new FileInfo(absolutePath).LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// The two meanings of a path that decide where a login item really points. Both work on '/'-separated absolute paths as text (no
/// <c>Path.GetFullPath</c>, which would turn a macOS path into a drive path when the tests run on Windows).
/// </summary>
internal static class ProgramPaths
{
    /// <summary>More links than this on one path is taken for a loop (the kernel's own limit is 32 on macOS).</summary>
    internal const int MaxLinkHops = 40;

    /// <summary>
    /// The path with empty segments, <c>.</c> and <c>..</c> taken out - what <c>Path.GetFullPath</c> gives for an absolute path - without
    /// looking at the disk. <c>..</c> above the root stays at the root.
    /// </summary>
    public static string Lexical(string absolute)
    {
        var parts = new List<string>();
        foreach (var segment in absolute.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>
    /// The path the system really reaches: every symbolic link in it - in the folders on the way and in the file itself - is replaced by what
    /// it points to, and <c>..</c> is applied to the folder it was really reached through (after a link, that is not the folder written
    /// before it). The part of the path that does not exist yet is kept as written. Null when the links loop.
    /// </summary>
    public static string? Physical(string absolute, ILinkReader links)
    {
        var pending = new Stack<string>();
        foreach (var segment in absolute.Split('/').Reverse()) pending.Push(segment);

        var resolved = new List<string>();
        var hops = 0;
        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var candidate = "/" + string.Join('/', resolved.Append(segment));
            var target = links.ReadLink(candidate);
            if (target is null)
            {
                resolved.Add(segment);
                continue;
            }

            if (++hops > MaxLinkHops) return null;
            if (target.StartsWith('/')) resolved.Clear();   // an absolute target starts over from the root; a relative one from the folder the link is in
            foreach (var part in target.Split('/').Reverse()) pending.Push(part);
        }
        return "/" + string.Join('/', resolved);
    }
}
