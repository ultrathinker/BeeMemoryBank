using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BeeMemoryBank.BlindDesktop.ViewModels;

namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>
/// The one window. Closing it only hides it (the tray icon is the persistent control); the app ends with an explicit Quit, or
/// when the operating system ends the session.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            using var icon = AssetLoader.Open(new Uri("avares://BeeMemoryBank.BlindDesktop/Assets/icon.png"));
            Icon = new WindowIcon(icon);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            // A missing icon is cosmetic.
        }

        // A timer tick on the UI thread: an exception that leaves it is an unhandled UI exception. RefreshAsync reports its own failures;
        // this is the belt over the braces.
        _poll.Tick += async (_, _) =>
        {
            try
            {
                if (_viewModel is not null) await _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Refreshing the window failed", ex);
            }
        };
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    /// <summary>True once the app is quitting; until then closing the window hides it.</summary>
    public bool Quitting { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _poll.Start();
        if (_viewModel is not null) _ = _viewModel.RefreshAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Quitting && e.CloseReason is not (WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown))
        {
            e.Cancel = true;
            _poll.Stop();
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>Shows the window (or brings it forward) and reads the status at once.</summary>
    public void ShowAndFocus()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _poll.Start();
        if (_viewModel is not null) _ = _viewModel.RefreshAsync();
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.CopyRequested -= OnCopyRequested;
        }
        _viewModel = viewModel;
        if (viewModel is null) return;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.CopyRequested += OnCopyRequested;
        ShowQr(viewModel.PairingQrPng);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PairingQrPng) && _viewModel is not null)
            Dispatcher.UIThread.Post(() => ShowQr(_viewModel.PairingQrPng));
    }

    private void ShowQr(byte[]? png)
    {
        try
        {
            var old = QrImage.Source as IDisposable;
            QrImage.Source = png is null ? null : new Bitmap(new MemoryStream(png));
            old?.Dispose();
        }
        catch (Exception ex)
        {
            // The text of the code is on the screen as well; a picture that cannot be made is not worth the app.
            ErrorLog.Write("Showing the QR picture failed", ex);
            QrImage.Source = null;
        }
    }

    /// <summary>An <c>async void</c> handler: an exception in it would be raised on the UI thread and end the app, so it is caught here.</summary>
    private async void OnCopyRequested(string text)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copying to the clipboard failed", ex);
        }
    }
}
