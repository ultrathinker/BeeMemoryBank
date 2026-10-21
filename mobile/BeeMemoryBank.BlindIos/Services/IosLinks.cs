namespace BeeMemoryBank.BlindIos.Services;

/// <summary>What a link that opened the app is.</summary>
public enum IosLinkKind
{
    /// <summary>Not one of ours.</summary>
    None,

    /// <summary><c>bmb-blind-call:</c> - the computer's answer to a pairing code (the Camera scanned its QR code).</summary>
    CallCode,

    /// <summary><c>bmb-invite:</c> - a one-time invitation from the owner's computer (the Camera scanned its QR code). It goes to the Add page, never straight into an account.</summary>
    Invitation,
}

/// <summary>A link and the text to hand on: the whole link, as AppCore reads it.</summary>
public readonly record struct IosLink(IosLinkKind Kind, string Text)
{
    public bool IsOurs => Kind != IosLinkKind.None;
}

/// <summary>
/// Tells the two links the app is the handler of apart. The system Camera opens a custom-scheme link in the app that registered the scheme (Info.plist), so a QR code needs no scanner
/// of the app's own. Only the scheme is judged here; the text is read, checked and previewed by AppCore (a call code only by the account whose pairing secret made it, an invitation by
/// <c>PreviewInvitation</c> and only added when the person presses Add).
/// </summary>
public static class IosLinks
{
    public const string CallCodeScheme = "bmb-blind-call";
    public const string InvitationScheme = "bmb-invite";

    /// <summary>The longest link taken. An invitation is a few hundred characters; anything far beyond that is not one, and a link is handed to the screen as it is.</summary>
    public const int MaxLength = 4096;

    public static IosLink Parse(string? url)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return new IosLink(IosLinkKind.None, "");
        if (HasScheme(text, CallCodeScheme)) return new IosLink(IosLinkKind.CallCode, text);
        if (HasScheme(text, InvitationScheme)) return new IosLink(IosLinkKind.Invitation, text);
        return new IosLink(IosLinkKind.None, "");
    }

    private static bool HasScheme(string text, string scheme) =>
        text.Length > scheme.Length && text[scheme.Length] == ':' && text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase);
}
