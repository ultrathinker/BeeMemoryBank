using System.Globalization;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos;

/// <summary>
/// The little display rules the tree, search and note screens share, copied from the Android
/// app's ContentFormat: the human, localized, local-time date (today / yesterday by name, this
/// year as "14 Nov" day-first in Russian, older with the year), the row date without the clock,
/// the localized item count with the Russian plural forms, and the root folder's name. Only the
/// display changes; the stored and compared values are untouched. Lives outside Services/
/// because the test project compiles Services/*.cs without MAUI and without the localization.
/// </summary>
public static class ContentFormat
{
    /// <summary>A whole path for a person: the root reads its localized name, never a bare slash; other folders keep their path.</summary>
    public static string DisplayPath(string path)
    {
        var normalized = NotesService.NormalizePath(path);
        return normalized == "/" ? AppStrings.Get("TreeRoot") : normalized;
    }

    /// <summary>The last segment of a folder path; the root keeps its localized name.</summary>
    public static string FolderName(string path)
    {
        var normalized = NotesService.NormalizePath(path);
        if (normalized == "/") return AppStrings.Get("TreeRoot");
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>"3 items" with the Russian plural forms (one/few/many); English one/other.</summary>
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

    /// <summary>The short date of a row: human, in the phone's language, local time, without the clock.</summary>
    public static string ShortDate(DateTime utc) => HumanDate(utc, withTime: false);

    /// <summary>The date and time the note meta bar shows.</summary>
    public static string FullDate(DateTime utc) => HumanDate(utc, withTime: true);

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
