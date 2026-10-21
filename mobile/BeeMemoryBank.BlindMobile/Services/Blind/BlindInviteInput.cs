using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What the gate made of a text from outside: the text for the paste box, a sentence for the screen, or nothing (empty, cancelled).</summary>
public readonly record struct BlindInviteInputResult(string? Text, string? Refusal)
{
    public static readonly BlindInviteInputResult Nothing = new(null, null);
}

/// <summary>
/// The one gate every text that comes from outside passes before it is put into the Add screen's box: the clipboard, the QR scanner, a link the system handed over. Another app owns the clipboard and a QR
/// code is whatever its maker printed, so the text is a hostile string until it has passed: one longer than an invitation can be (<see cref="BlindInvitation.MaxTextLength"/>) is refused BEFORE it is trimmed, copied
/// or given to the box (the box, its change callback and the preview would each handle a string of any size), with a sentence that does not repeat any of it. What passes is trimmed (a clipboard often ends in a
/// line break); whether it IS an invitation is the preview's question, answered from the text alone.
/// </summary>
public static class BlindInviteInput
{
    /// <summary>The longest text the box takes: the longest invitation there can be. The XAML's <c>MaxLength</c> is this number (a test pins both).</summary>
    public const int MaxLength = BlindInvitation.MaxTextLength;

    /// <summary>Room for the white space around an invitation that was copied with it; anything longer than this is refused without being looked at.</summary>
    internal const int Slack = 64;

    public static readonly string TooLong = $"That text is too long to be an invitation (an invitation is at most {MaxLength} characters). Nothing was pasted.";

    /// <summary>Judges a text from outside. Null or blank is "nothing". Never throws, never copies a text that is over the limit.</summary>
    public static BlindInviteInputResult Accept(string? text)
    {
        if (string.IsNullOrEmpty(text)) return BlindInviteInputResult.Nothing;
        if (text.Length > MaxLength + Slack) return new(null, TooLong);

        var trimmed = text.Trim();
        if (trimmed.Length > MaxLength) return new(null, TooLong);
        return trimmed.Length == 0 ? BlindInviteInputResult.Nothing : new(trimmed, null);
    }

    /// <summary>The clipboard's text through the gate. A clipboard that cannot be read is the caller's failure to say, in its own fixed sentence.</summary>
    public static async Task<BlindInviteInputResult> FromClipboardAsync(Func<Task<string?>> read) => Accept(await read().ConfigureAwait(false));

    /// <summary>What the scanner read through the gate. A scanner that was cancelled (null) is "nothing".</summary>
    public static async Task<BlindInviteInputResult> FromScannerAsync(IBlindQrScanner scanner, CancellationToken ct = default) =>
        Accept(await scanner.ScanAsync(ct).ConfigureAwait(false));
}
