namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The list rows' density, shared by the tree and the search and remembered across launches (the
/// Android tree's toggle): compact is one truncated line, expanded wraps up to three. One preference
/// key, so a toggle on either page is picked up by the other when it appears.
/// </summary>
public static class ListDensity
{
    public const string Key = "list_items_expanded";

    public static bool Expanded
    {
        get => Preferences.Get(Key, false);
        set => Preferences.Set(Key, value);
    }

    /// <summary>Raised after a toggle so the shown pages re-read the preference.</summary>
    public static event EventHandler? Changed;

    public static void Toggle()
    {
        Expanded = !Expanded;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static LineBreakMode LineBreakMode => Expanded ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
    public static int MaxLines => Expanded ? 3 : 1;
}
