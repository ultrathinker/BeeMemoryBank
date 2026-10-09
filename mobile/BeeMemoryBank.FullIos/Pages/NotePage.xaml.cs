using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// A note, rendered on the phone (NoteHtml: no script, nothing loaded from anywhere). A link in it opens in the browser only after the
/// person agrees. A note protected by its own passphrase stays closed until the passphrase is given; the passphrase lives only as long as
/// this page and the editor it opens.
/// </summary>
public partial class NotePage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private Guid _id;
    private NoteView? _note;
    private string? _openedText;
    private string? _passphrase;

    public NotePage(IServiceProvider services, NotesService notes)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
    }

    public NotePage For(Guid id)
    {
        _id = id;
        return this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _note = await Task.Run(() => _notes.GetAsync(_id));
        }
        catch (Exception ex)
        {
            Show(NoteHtml.Text("This note could not be opened: " + ex.Message));
            return;
        }
        if (_note is null)
        {
            await DisplayAlertAsync("Note", "This note is no longer here (it was deleted, here or on another device).", "OK");
            await Navigation.PopAsync();
            return;
        }

        Title = _note.Title;
        TitleLabel.Text = _note.Title;
        MetaLabel.Text = $"{_note.Path} · {_note.UpdatedAt.ToLocalTime():d MMM yyyy, HH:mm}";
        if (!_note.IsProtected)
        {
            _openedText = _note.Body;
            ShowText(_openedText);
            return;
        }
        if (_passphrase is not null && NotesService.OpenProtected(_note.Body, _passphrase) is { } text)
        {
            _openedText = text;
            ShowText(text);
            return;
        }
        _openedText = null;
        Body.IsVisible = false;
        LockedCard.IsVisible = true;
        EditItem.IsEnabled = false;
        HintLabel.Text = string.IsNullOrWhiteSpace(_note.ProtectionHint) ? "" : "Hint: " + _note.ProtectionHint;
        HintLabel.IsVisible = !string.IsNullOrWhiteSpace(_note.ProtectionHint);
    }

    private void ShowText(string markdown)
    {
        LockedCard.IsVisible = false;
        Body.IsVisible = true;
        EditItem.IsEnabled = true;
        Show(NoteHtml.Render(markdown));
    }

    private void Show(string html)
    {
        Body.Source = new HtmlWebViewSource { Html = html };
    }

    private async void OnOpenProtected(object? sender, EventArgs e)
    {
        if (_note is null) return;
        var passphrase = PassphraseEntry.Text ?? "";
        // The field is emptied whatever opening ends in (SecretFields): opened, refused, or an exception.
        var text = await SecretFields.RunAsync(() => Task.Run(() => NotesService.OpenProtected(_note.Body, passphrase)), () => PassphraseEntry.Text = "");
        if (text is null)
        {
            PassphraseError.Text = "That is not this note's passphrase.";
            PassphraseError.IsVisible = true;
            return;
        }
        PassphraseError.IsVisible = false;
        _passphrase = passphrase;
        _openedText = text;
        ShowText(text);
    }

    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        // The note's own page (an HTML string: about:blank or the app's file base) loads; nothing else ever does inside the view. A link
        // the person tapped is cancelled here and, for web and mail links, offered to the system after a question.
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var target) && IsThePageItself(target)) return;
        e.Cancel = true;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto")) return;
        if (await DisplayAlertAsync("Open link", uri.ToString(), "Open", "Cancel"))
            await Launcher.Default.OpenAsync(uri);
    }

    /// <summary>about:blank, or the app bundle the HTML string is loaded against - never another file, never the network.</summary>
    private static bool IsThePageItself(Uri uri) =>
        uri.Scheme == "about" || (uri.IsFile && uri.AbsolutePath.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal));

    private async void OnEdit(object? sender, EventArgs e)
    {
        if (_note is null || _openedText is null) return;
        await Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().Existing(_note, _openedText, _passphrase));
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (_note is null) return;
        if (!await DisplayAlertAsync("Move to trash", $"Move \"{_note.Title}\" to the trash? It can be restored from there, also on your other devices.", "Move to trash", "Cancel"))
            return;
        try
        {
            await _notes.DeleteAsync(_note.Id);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Move to trash", ex.Message, "OK");
        }
    }
}
