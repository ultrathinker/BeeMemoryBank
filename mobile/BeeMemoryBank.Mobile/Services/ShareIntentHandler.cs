namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Holds text shared into the app from external sources (Android share intent).
/// ArticleEditPage reads and clears this on appearing.
/// </summary>
public static class ShareIntentHandler
{
    private static volatile string? _pendingText;

    public static string? PendingText { get => _pendingText; set => _pendingText = value; }
}
