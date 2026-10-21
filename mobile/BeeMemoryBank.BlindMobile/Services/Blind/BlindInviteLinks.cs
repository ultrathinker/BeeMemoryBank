using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The hand-over point between the system (a <c>bmb-invite:</c> link the person tapped or scanned with the Camera) and the screen that shows its preview. The link is a one-time capability: it is
/// kept in memory only, until the Add screen takes it; it is never logged, never written, and never acted on by itself - the person sees the preview and presses Add. A text that is not a link of this app, or
/// longer than an invitation can be, is refused here, so that another app cannot make this one hold an arbitrary string.
/// </summary>
public static class BlindInviteLinks
{
    public const string Scheme = "bmb-invite:";

    private static readonly object Gate = new();
    private static string? _pending;

    /// <summary>Raised (on any thread) when a link is waiting; the screen takes it with <see cref="Take"/>.</summary>
    public static event Action? Offered;

    /// <summary>Keeps <paramref name="text"/> for the Add screen when it is a link of this app. Returns false (and keeps nothing) otherwise.</summary>
    public static bool Offer(string? text)
    {
        if (!IsLink(text)) return false;
        lock (Gate) _pending = text;
        try { Offered?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* a screen that is gone must not break the system's hand-over */ }
        return true;
    }

    /// <summary>The waiting link, once; null when there is none.</summary>
    public static string? Take()
    {
        lock (Gate)
        {
            var text = _pending;
            _pending = null;
            return text;
        }
    }

    public static bool IsLink(string? text) =>
        text is { Length: > 0 } && text.Length <= BlindInvitation.MaxTextLength && text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
}
