using System.Globalization;
using System.Text.RegularExpressions;
using BeeMemoryBank.Mobile.Localization;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// The little text rules the tree and search rows share: a canonical folder path, a plain preview
/// of a markdown body (the mock-up's getPlainPreview), the localized item count with the Russian
/// plural forms, and the short date the rows show.
/// </summary>
public static class ContentFormat
{
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    /// <summary>True when <paramref name="candidate"/> is a folder directly inside <paramref name="parent"/>.</summary>
    public static bool IsDirectChildFolder(string parent, string candidate)
    {
        parent = NormalizePath(parent);
        candidate = NormalizePath(candidate);
        if (parent == "/")
            return candidate != "/" && !candidate.TrimStart('/').Contains('/');
        if (!candidate.StartsWith(parent + "/", StringComparison.Ordinal)) return false;
        return !candidate[(parent.Length + 1)..].Contains('/');
    }

    /// <summary>The last segment of a folder path; the root keeps its localized name.</summary>
    public static string FolderName(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == "/") return AppStrings.Get("TreeRoot");
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>A one-line plain preview of a markdown body: no images, code fences or markup.</summary>
    public static string PlainPreview(string markdown, int maxLength = 90)
    {
        var text = Regex.Replace(markdown, @"!\[[^\]]*\]\([^)]+\)", "");
        text = Regex.Replace(text, @"```[\s\S]*?```", "");
        text = Regex.Replace(text, @"^#+\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"[*_`>-]", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    /// <summary>"3 items" with the Russian plural forms (one/few/many) — the mock-up's rule; English one/other.</summary>
    public static string ItemCount(int n)
    {
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru")
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11) return AppStrings.Format("ItemCountOne", n);
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return AppStrings.Format("ItemCountFew", n);
            return AppStrings.Format("ItemCountMany", n);
        }
        return AppStrings.Format(n == 1 ? "ItemCountOne" : "ItemCountOther", n);
    }

    /// <summary>"3 articles" with the Russian plural forms (one/few/many) — a tag counts articles,
    /// not generic items. The number itself is computed exactly as before.</summary>
    public static string ArticleCount(int n)
    {
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru")
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11) return AppStrings.Format("ArticleCountOne", n);
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return AppStrings.Format("ArticleCountFew", n);
            return AppStrings.Format("ArticleCountMany", n);
        }
        return AppStrings.Format(n == 1 ? "ArticleCountOne" : "ArticleCountOther", n);
    }

    // Strip non-data: image links and dangerous raw HTML before native rendering. Articles can be
    // authored on any peer; without this an external image (e.g. ![](http://attacker/pixel.png))
    // would be auto-fetched by the native Image control, leaking the reader's IP/read-activity and
    // defeating the E2E privacy of the vault. Media is already inlined as data: URIs upstream.
    private static readonly Regex ExternalImage =
        new(@"!\[([^\]]*)\]\(\s*(?!data:)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex DangerousHtml =
        new(@"<\s*/?\s*(script|iframe|object|embed|form|meta|link|style|base)\b[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The markdown safe to render natively: no external images, no dangerous HTML.</summary>
    public static string SanitizeMarkdown(string markdown)
    {
        markdown = ExternalImage.Replace(markdown, $"*{Localization.AppStrings.Get("ExternalImageRemoved")}*");
        markdown = DangerousHtml.Replace(markdown, string.Empty);
        markdown = ShowInlineCode(markdown);
        return markdown;
    }

    // Indiko.Maui.Controls.Markdown 1.5.0 has no CodeSpan branch in its inline renderer (verified by
    // decompiling RenderInlines), so an inline code span is dropped whole - "Copies `a.txt`" rendered
    // as "Copies ". The library offers no inline monospace/background hook either. Make the code text
    // visible by turning each inline span into bold text before rendering. Fenced code blocks are
    // matched first and returned unchanged, so they (and tables) are untouched.
    private static readonly Regex CodeOrFence = new(@"```[\s\S]*?```|`([^`\r\n]+)`", RegexOptions.Compiled);

    private static string ShowInlineCode(string markdown)
    {
        return CodeOrFence.Replace(markdown, m =>
        {
            if (m.Value.StartsWith("```", StringComparison.Ordinal)) return m.Value;
            var code = m.Groups[1].Value;
            code = code.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_");
            return "**" + code + "**";
        });
    }

    /// <summary>The short date of a row: human, in the phone's language, local time, without the clock.</summary>
    public static string ShortDate(DateTime utc) => HumanDate(utc, withTime: false);

    /// <summary>The date and time the article meta bar and the sync State card show.</summary>
    public static string FullDate(DateTime utc) => HumanDate(utc, withTime: true);

    /// <summary>
    /// A human, localized, local-time date: today / yesterday by name, this year as "14 Nov" (day-first
    /// in Russian, month-first in English), older with the year and no clock. Only the display changes;
    /// the stored and compared values are untouched.
    /// </summary>
    private static string HumanDate(DateTime utc, bool withTime)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Now.Date;
        var date = local.Date;
        var culture = CultureInfo.CurrentUICulture;
        bool ru = culture.TwoLetterISOLanguageName == "ru";
        string time = local.ToString("HH:mm", culture);

        if (date == today)
            return withTime ? $"{AppStrings.Get("DateToday")}, {time}" : AppStrings.Get("DateToday");
        if (date == today.AddDays(-1))
            return withTime ? $"{AppStrings.Get("DateYesterday")}, {time}" : AppStrings.Get("DateYesterday");

        string month = culture.DateTimeFormat.AbbreviatedMonthNames[local.Month - 1];
        string dayMonth = ru ? $"{local.Day} {month}" : $"{month} {local.Day}";
        if (local.Year != today.Year)
            return ru ? $"{local.Day} {month} {local.Year}" : $"{month} {local.Day}, {local.Year}";
        return withTime ? $"{dayMonth}, {time}" : dayMonth;
    }
}
