using GmsTasks = Android.Gms.Tasks;
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
        // Not a cancellation: Play services is missing, out of date, or could not fetch the scanner module. The sentence names no detail of the phone.
        static Exception Unavailable(Exception ex) => new InvalidOperationException("The QR scanner of Google Play services is not available on this phone. Paste the invitation or open its link instead.", ex);
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
            // The person closing the scanner completes it with null: nothing was read.
            return await scan.WaitAsync(ct);
        }
        catch (System.OperationCanceledException)
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
