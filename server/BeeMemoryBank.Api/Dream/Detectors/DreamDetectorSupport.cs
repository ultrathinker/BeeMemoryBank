using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The pure helpers the rule detectors share (plan 3.5, 3.6): what "the same tag", "the same title", "the same
/// text" and "a test folder" mean, and the few path and truncation operations every detector needs. Keeping them
/// in one place stops the detectors from growing subtly different notions of equality.
/// </summary>
internal static class DreamDetectorSupport
{
    /// <summary>A tag name as plan 3.6 compares it: lower case, with every white-space character and every
    /// character of <see cref="DreamThresholds.TagNormalizeRemovedChars"/> removed.</summary>
    public static string NormalizeTagName(string name, DreamThresholds t)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || t.TagNormalizeRemovedChars.Contains(c)) continue;
            text.Append(char.ToLowerInvariant(c));
        }
        return text.ToString();
    }

    /// <summary>A title or a body for comparison: lower case, every run of white space folded to one space,
    /// trimmed (plan 3.6: "lower case, collapsed white space").</summary>
    public static string FoldWhiteSpace(string text)
    {
        var lower = text.ToLowerInvariant();
        var folded = new StringBuilder(lower.Length);
        var pendingSpace = false;
        foreach (var c in lower)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = folded.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                folded.Append(' ');
                pendingSpace = false;
            }
            folded.Append(c);
        }
        return folded.ToString();
    }

    /// <summary>SHA-256 of a normalized text, lowercase hex. The plan compares the hash, not the strings, in case a
    /// body stays in memory between two uses.</summary>
    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The last segment of a tree path; "/" for the root.</summary>
    public static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return "/";
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>True when a folder name looks like a test leftover: leading underscores dropped, then compared
    /// case-insensitively with <see cref="DreamThresholds.TestFolderNames"/> (plan 3.6).</summary>
    public static bool IsTestFolderName(string name, DreamThresholds t)
    {
        var trimmed = name.TrimStart('_');
        foreach (var candidate in t.TestFolderNames)
        {
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The canonical form of a tree path; an invalid path is compared as it came.</summary>
    public static string CanonicalPath(string path) => TreePathCanonicalizer.TryCanonicalize(path) ?? path.Trim();

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="ancestor"/>.</summary>
    public static bool IsAncestor(string ancestor, string path) =>
        ancestor == "/" ? path != "/" : path.StartsWith(ancestor + "/", StringComparison.Ordinal);

    /// <summary>A text cut to a stored length, three dots marking the cut; a surrogate pair is never split.</summary>
    public static string Clamp(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        if (maxLength <= 3) return text[..Math.Max(0, maxLength)];
        var cut = maxLength - 3;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "...";
    }
}
