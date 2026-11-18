using AVFoundation;
using BeeMemoryBank.FullIos.Localization;
using CoreFoundation;
using Foundation;
using UIKit;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// "Scan QR code" on the join page: the computer shows its join code as a QR code, and opening it through the Camera app
/// was not obvious. This puts the camera full screen inside the app and returns the text of the first QR code it reads. Nothing is
/// sent anywhere: the text only fills in the join code field, and the join still waits for the password and the Join button.
/// </summary>
internal static class QrScanner
{
    /// <summary>
    /// The scanned text, or a sentence for the person when there is none to give (camera not allowed, no camera). Both null when
    /// the person closed the camera.
    /// </summary>
    public static async Task<(string? Text, string? Problem)> ScanAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (status == AVAuthorizationStatus.NotDetermined)
            status = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video)
                ? AVAuthorizationStatus.Authorized
                : AVAuthorizationStatus.Denied;
        if (status != AVAuthorizationStatus.Authorized) return (null, AppStrings.Get("ScanQrDenied"));

        return await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
            var top = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();
            if (device is null || top is null) return ((string?)null, (string?)AppStrings.Get("ScanQrNoCamera"));

            var scanner = new ScannerController(device) { ModalPresentationStyle = UIModalPresentationStyle.FullScreen };
            await top.PresentViewControllerAsync(scanner, true);
            var text = await scanner.Result;
            return (text, scanner.Failed ? AppStrings.Get("ScanQrNoCamera") : null);
        });
    }

    /// <summary>The camera picture, a line of help and Cancel; closes itself on the first QR code.</summary>
    private sealed class ScannerController(AVCaptureDevice device) : UIViewController
    {
        private readonly TaskCompletionSource<string?> _done = new();
        private AVCaptureSession? _session;
        private AVCaptureVideoPreviewLayer? _preview;
        private Reader? _reader;

        public Task<string?> Result => _done.Task;

        /// <summary>The camera could not be opened (the sheet closed by itself).</summary>
        public bool Failed { get; private set; }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.Black;

            var input = AVCaptureDeviceInput.FromDevice(device, out _);
            var session = new AVCaptureSession();
            var output = new AVCaptureMetadataOutput();
            if (input is null || !session.CanAddInput(input)) { Fail(); return; }
            session.AddInput(input);
            if (!session.CanAddOutput(output)) { Fail(); return; }
            session.AddOutput(output);
            _reader = new Reader(OnFound);
            output.SetDelegate(_reader, DispatchQueue.MainQueue);
            output.MetadataObjectTypes = AVMetadataObjectType.QRCode;
            _session = session;

            _preview = new AVCaptureVideoPreviewLayer(session) { VideoGravity = AVLayerVideoGravity.ResizeAspectFill, Frame = View.Bounds };
            View.Layer.AddSublayer(_preview);

            var help = new UILabel
            {
                Text = AppStrings.Get("ScanQrHelp"),
                TextColor = UIColor.White,
                TextAlignment = UITextAlignment.Center,
                Lines = 0,
                Font = UIFont.PreferredBody!, // never null for a built-in text style
                AdjustsFontForContentSizeCategory = true,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            var cancel = UIButton.FromType(UIButtonType.System);
            cancel.SetTitle(AppStrings.Get("Cancel"), UIControlState.Normal);
            cancel.SetTitleColor(UIColor.White, UIControlState.Normal);
            cancel.TitleLabel!.Font = UIFont.PreferredHeadline!;
            cancel.TitleLabel.AdjustsFontForContentSizeCategory = true;
            cancel.TranslatesAutoresizingMaskIntoConstraints = false;
            cancel.TouchUpInside += (_, _) => Close(null);
            View.AddSubviews(help, cancel);

            var guide = View.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                help.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 24),
                help.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -24),
                help.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 24),
                cancel.CenterXAnchor.ConstraintEqualTo(guide.CenterXAnchor),
                cancel.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -24),
                cancel.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
            ]);
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            if (_preview is not null) _preview.Frame = View!.Bounds;
        }

        public override void ViewDidAppear(bool animated)
        {
            base.ViewDidAppear(animated);
            if (Failed) { Close(null); return; }
            // StartRunning blocks until the camera is up; Apple asks for it off the main thread.
            var session = _session;
            if (session is not null) Task.Run(session.StartRunning);
        }

        public override void ViewWillDisappear(bool animated)
        {
            base.ViewWillDisappear(animated);
            _session?.StopRunning();
            _done.TrySetResult(null);
        }

        private void Fail() => Failed = true;

        private void OnFound(string text)
        {
            if (_done.Task.IsCompleted) return;
            new UINotificationFeedbackGenerator().NotificationOccurred(UINotificationFeedbackType.Success);
            Close(text);
        }

        private void Close(string? text)
        {
            _session?.StopRunning();
            _done.TrySetResult(text);
            DismissViewController(true, null);
        }
    }

    /// <summary>Hands the first non-empty QR text to the controller (on the main queue, where the delegate was set).</summary>
    private sealed class Reader : AVCaptureMetadataOutputObjectsDelegate
    {
        private readonly Action<string> _found;

        public Reader(Action<string> found) => _found = found;

        public override void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
        {
            foreach (var item in metadataObjects)
                if (item is AVMetadataMachineReadableCodeObject { StringValue: { Length: > 0 } text })
                {
                    _found(text);
                    return;
                }
        }
    }
}
