namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Reads a join code QR with the system's own scanner screen (Google Play services on Android): the app needs and asks for no CAMERA
/// permission. Returns the scanned text, or null when the person closed the scanner (the field stays as it was).
/// </summary>
public interface IQrScanner
{
    Task<string?> ScanAsync(CancellationToken ct = default);
}

/// <summary>The scanner of Play services is not available on this phone (missing, out of date, or its module could not be fetched). The page says one plain sentence of its own.</summary>
public sealed class QrScannerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
