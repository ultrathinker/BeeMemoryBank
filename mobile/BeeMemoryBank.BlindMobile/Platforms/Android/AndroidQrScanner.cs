using Android.Gms.Extensions;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// Reads an invitation QR code with Google Play services' code scanner (<c>play-services-code-scanner</c>): the scanning screen is Google's, shown by Play services, so the app needs and asks for no CAMERA
/// permission. Only QR codes are looked for. The scanner returns text; the Add screen puts it in the paste box, where it gets the same preview as any pasted invitation, and nothing is added until the person
/// presses Add. A phone without Play services (or whose scanner module is not there yet and cannot be fetched) gets a sentence that says to paste or open the link instead.
/// </summary>
public sealed class AndroidQrScanner : IBlindQrScanner
{
    public async Task<string?> ScanAsync(CancellationToken ct = default)
    {
        var options = new GmsBarcodeScannerOptions.Builder()
            .SetBarcodeFormats(Barcode.FormatQrCode)
            .Build();
        var scanner = GmsBarcodeScanning.GetClient(Platform.AppContext, options);
        try
        {
            var barcode = await scanner.StartScan().AsAsync<Barcode>().WaitAsync(ct);
            return barcode?.RawValue;
        }
        catch (System.OperationCanceledException)
        {
            // The person closed the scanner (the task is cancelled), or this call was cancelled: nothing was read.
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a cancellation: Play services is missing, out of date, or could not fetch the scanner module. The sentence names no detail of the phone.
            throw new InvalidOperationException("The QR scanner of Google Play services is not available on this phone. Paste the invitation or open its link instead.", ex);
        }
    }
}
