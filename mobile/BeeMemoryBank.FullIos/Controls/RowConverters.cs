using System.Globalization;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>The row date: human, local, without the clock (the design's list rows).</summary>
public sealed class ShortDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime at ? ContentFormat.ShortDate(at) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A path for a row: the root shows its localized name, not a bare slash.</summary>
public sealed class DisplayPathConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path ? (NotesService.NormalizePath(path) == "/" ? AppStrings.Get("TreeRoot") : path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
