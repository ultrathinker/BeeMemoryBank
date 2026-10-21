using System.Globalization;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Sizes as a person writes and reads them: "2 GB", "200 MB", "1.4 GB", "512 KB". KB, MB, GB and TB are 1024-based here (as the device's own storage screens count them), and
/// the same letters with an "i" (MiB, GiB) mean the same. Pure and culture-free: a comma is accepted as the decimal mark, the output always uses a dot.
/// </summary>
public static partial class BlindSizeFormat
{
    private const long Kilo = 1024;
    private const long Mega = Kilo * 1024;
    private const long Giga = Mega * 1024;
    private const long Tera = Giga * 1024;

    /// <summary>The largest size a form accepts: a petabyte is more than any phone holds and keeps the arithmetic far from an overflow.</summary>
    public const long MaxParsed = Tera * 1024;

    /// <summary>"0 B", "512 KB", "38 MB", "1.4 GB", "2 GB": one decimal below ten of a unit, none from ten up, trailing ".0" dropped.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < Kilo) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / (double)Kilo;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var text = Round(value);
        if (text == "1024" && unit < units.Length - 1)
        {
            // 1023.6 KB rounds to "1024 KB": say 1 MB.
            value /= 1024;
            unit++;
            text = Round(value);
        }
        return $"{text} {units[unit]}";
    }

    private static string Round(double value) =>
        value < 10
            ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads "2 GB", "200mb", "1,5 GB", "512 KiB", "0" (and "0 B"). Not an empty text (the caller decides what empty means). A number without a unit is refused unless it is 0:
    /// "200" could be bytes or megabytes, and a limit is not a place to guess.
    /// </summary>
    public static bool TryParse(string? text, out long bytes, out string? problem)
    {
        bytes = 0;
        problem = null;
        var match = SizePattern().Match(text ?? "");
        if (!match.Success)
        {
            problem = "Write a size like 200 MB or 2 GB.";
            return false;
        }

        // A text of any length can match the pattern: it is parsed without throwing, and checked against the range BEFORE it is multiplied, so that no input can overflow.
        const string tooBig = "That is more than a device can hold. Write a smaller size.";
        if (!decimal.TryParse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            problem = tooBig;
            return false;
        }
        var unit = match.Groups["u"].Value.ToUpperInvariant().Replace("I", "");
        if (unit.Length == 0)
        {
            if (number == 0) return true;
            problem = "Add a unit: KB, MB or GB (for example 200 MB).";
            return false;
        }

        var factor = unit switch { "KB" => Kilo, "MB" => Mega, "GB" => Giga, "TB" => Tera, _ => 1L };
        if (number > (decimal)MaxParsed / factor)
        {
            problem = tooBig;
            return false;
        }
        var product = number * factor;
        bytes = (long)Math.Round(product, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"^\s*(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[KMGT]i?B|B)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}
