using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Threading;
using BeeMemoryBank.BlindDesktop.ViewModels;

namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>
/// The one window. Closing it only hides it (the tray icon is the persistent control); the app ends with an explicit Quit, or
/// when the operating system ends the session. While it is open it reads the accounts every 3 seconds and asks about waiting invitations
/// every 15 seconds (the view model's watch).
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
        _viewModel?.OnWindowShown();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Quitting && e.CloseReason is not (WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown))
        {
            e.Cancel = true;
            _poll.Stop();
            _viewModel?.OnWindowHidden();
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>Shows the window (or brings it forward) and reads the accounts at once.</summary>
    public void ShowAndFocus()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _poll.Start();
        _viewModel?.OnWindowShown();
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.CopyRequested -= OnCopyRequested;
            _viewModel.Add.PasteRequested -= OnPasteRequested;
        }
        _viewModel = viewModel;
        if (viewModel is null) return;
        viewModel.CopyRequested += OnCopyRequested;
        viewModel.Add.PasteRequested += OnPasteRequested;
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

    /// <summary>"Paste" on the Add page: what is on the clipboard goes into the paste box (and with it the preview). Paste is the only way in on a desktop.</summary>
    private async void OnPasteRequested()
    {
        try
        {
            if (Clipboard is { } clipboard && _viewModel is { } viewModel)
            {
                var text = await clipboard.TryGetTextAsync();
                // Not trimmed here: the box bounds the text before anything copies it (see AddAccountViewModel.InviteText).
                if (!string.IsNullOrWhiteSpace(text)) viewModel.Add.InviteText = text;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Reading the clipboard failed", ex);
        }
    }
}
