namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The longest name a person may give this computer when they restore it from a blind node (srvfix9): the setup page, the restore client and the restore endpoint all hold to it, and the
/// blind node's claim (<c>POST /api/blind/claim</c>, 16 KiB at most) carries the name, so a name past it would be refused 413 AFTER the restore had committed. 200 characters is far more
/// than any computer is called and far below what could make the claim too large (a name of 200 characters is under 1.3 KB even with every character escaped in JSON).
/// </summary>
public static class DeviceDisplayName
{
    public const int MaxLength = 200;

    /// <summary>The sentence to show when <paramref name="name"/> is too long, or null when it is fine (an empty name is the caller's to refuse or default).</summary>
    public static string? Problem(string? name) =>
        name is { Length: > MaxLength } ? $"The name of this computer is at most {MaxLength} characters." : null;
}
