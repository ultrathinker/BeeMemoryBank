namespace BeeMemoryBank.Core;

/// Sort names so that leading-underscore items (like "_WorkLog") come first,
/// then the rest in ordinal order. Matches the convention used by VS Code /
/// Finder / Windows Explorer.
public sealed class UnderscoreFirstComparer : IComparer<string>
{
    public static readonly UnderscoreFirstComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var xu = !string.IsNullOrEmpty(x) && x[0] == '_';
        var yu = !string.IsNullOrEmpty(y) && y[0] == '_';
        if (xu != yu) return xu ? -1 : 1;
        return string.CompareOrdinal(x, y);
    }
}
