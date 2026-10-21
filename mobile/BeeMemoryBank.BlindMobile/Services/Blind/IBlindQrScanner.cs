namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Reads one QR code with the platform's own scanner and returns its text. The Add screen offers "Scan a QR code" only when the app registers one. The scanner returns text and nothing more: the text goes
/// into the paste box, where it gets the same preview as a pasted invitation and is added only when the person presses Add.
/// </summary>
public interface IBlindQrScanner
{
    /// <summary>The text of the code, or null when the person cancelled or the code could not be read. Throws only for a scanner that cannot run on this device (its message is a sentence for the screen).</summary>
    Task<string?> ScanAsync(CancellationToken ct = default);
}
