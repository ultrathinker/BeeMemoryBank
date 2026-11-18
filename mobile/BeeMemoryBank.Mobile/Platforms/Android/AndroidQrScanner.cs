using GmsTasks = Android.Gms.Tasks;
using BeeMemoryBank.Mobile.Services;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// Reads a join code QR with Google Play services' code scanner (<c>play-services-code-scanner</c>, the one new package):
/// the scanning screen is Google's, shown by Play services, so the app needs and asks for no CAMERA permission. Only QR codes are looked
/// for. The scanned text goes to the join code field; closing the scanner returns null and leaves the field as it was. A phone without
/// Play services (or whose scanner module is not there yet and cannot be fetched) gets <see cref="QrScannerUnavailableException"/>; the
/// page says one plain sentence of its own, never the raw exception text. The same shape as the blind app's AndroidQrScanner.
/// </summary>
public sealed class AndroidQrScanner : IQrScanner
{
    public async Task<string?> ScanAsync(CancellationToken ct = default)
    {
        var options = new GmsBarcodeScannerOptions.Builder()
            .SetBarcodeFormats(Barcode.FormatQrCode)
            .Build();
        var scanner = GmsBarcodeScanning.GetClient(Platform.AppContext, options);
        // Not a cancellation: Play services is missing, out of date, or could not fetch the scanner module. No detail of the phone is named.
        static Exception Unavailable(Exception ex) => new QrScannerUnavailableException("The QR scanner of Google Play services is not available on this phone.", ex);
        System.Threading.Tasks.Task<string?> scan;
        try
        {
            scan = Scan(scanner, Unavailable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Unavailable(ex);
        }
        try
        {
            // The person closing the scanner completes it with null: nothing was read, the field stays as it was.
            return await scan.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // The Play services task, awaited through listeners of our own. Not the AsAsync wrapper of the bindings: when the person closes the scanner that wrapper
    // completes its TaskCompletionSource with a null exception, which throws on the main thread and closes the app (seen on a real phone, 16.11).
    private static System.Threading.Tasks.Task<string?> Scan(Xamarin.Google.MLKit.Vision.CodeScanner.IGmsBarcodeScanner scanner, Func<Exception, Exception> unavailable)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.StartScan()
            .AddOnSuccessListener(new SuccessListener(result => done.TrySetResult((result as Barcode)?.RawValue)))
            .AddOnCanceledListener(new CanceledListener(() => done.TrySetResult(null)))
            .AddOnFailureListener(new FailureListener(error => done.TrySetException(unavailable(error))));
        return done.Task;
    }

    private sealed class SuccessListener(Action<Java.Lang.Object?> then) : Java.Lang.Object, GmsTasks.IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result) => then(result);
    }

    private sealed class CanceledListener(Action then) : Java.Lang.Object, GmsTasks.IOnCanceledListener
    {
        public void OnCanceled() => then();
    }

    private sealed class FailureListener(Action<Exception> then) : Java.Lang.Object, GmsTasks.IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception e) => then(e);
    }
}
