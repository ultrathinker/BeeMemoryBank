using System.Text.RegularExpressions;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// The editor's pure text rules, shared by the formatting toolbar and the image button and run by
/// the tests: a snippet lands at the cursor, an image gets the next "image-NNN" alt text and its
/// Markdown reference is inserted at the cursor. No MAUI here, so the tests exercise the same code
/// the page calls. The Markdown reference shape matches the Android editor and the note view's
/// media resolver (/api/media/{id}).
/// </summary>
public static class EditorText
{
    /// <summary>Inserts <paramref name="snippet"/> at the cursor, clamped into the text.</summary>
    public static string InsertSnippet(string text, int cursor, string snippet)
    {
        text ??= "";
        var at = Math.Clamp(cursor, 0, text.Length);
        return text.Insert(at, snippet);
    }

    /// <summary>The next free "image-NNN" alt text, one past the highest already in the body.</summary>
    public static string NextImageAlt(string content)
    {
        var counter = 0;
        foreach (Match m in Regex.Matches(content ?? "", @"!\[image-(\d+)\]"))
            if (int.TryParse(m.Groups[1].Value, out var n) && n >= counter)
                counter = n + 1;
        return "image-" + counter.ToString().PadLeft(3, '0');
    }

    /// <summary>Inserts the image's Markdown reference on its own lines at the cursor.</summary>
    public static string InsertImageMarkdown(string content, int cursor, Guid mediaId)
    {
        var alt = NextImageAlt(content);
        var markdown = $"\n![{alt}](/api/media/{mediaId})\n";
        return InsertSnippet(content, cursor, markdown);
    }
}
