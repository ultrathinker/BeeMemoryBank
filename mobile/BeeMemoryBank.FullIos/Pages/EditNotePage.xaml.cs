using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// Writing a note: title, folder, Markdown text. Save is at the bottom, under the thumb, and in the bar. Leaving with unsaved changes asks
/// first. Typing counts as activity for the idle lock (the keyboard's touches are not the app's).
/// </summary>
public partial class EditNotePage : ContentPage
{
    private readonly NotesService _notes;
    private readonly AppFlow _flow;
    private readonly IServiceProvider _services;
    private readonly List<string> _tags = [];
    private Guid? _id;
    private string? _passphrase;
    private bool _dirty;
    private bool _loading;
    private bool _tagsLoaded;

    public EditNotePage(NotesService notes, AppFlow flow, IServiceProvider services)
    {
        InitializeComponent();
        _notes = notes;
        _flow = flow;
        _services = services;
        Shell.SetBackButtonBehavior(this, new BackButtonBehavior { Command = new Command(async () => await LeaveAsync()) });
        ModeSegments.Items = [AppStrings.Get("ModeText"), AppStrings.Get("ModePreview")];
        ModeSegments.SelectedIndex = 0;
        ModeSegments.SelectionChanged += (_, index) => SetPreviewMode(index == 1);
        // The password fields mark the note dirty too (PasswordBox has no TextChanged of its own).
        ProtectPassEntry.PropertyChanged += OnProtectFieldChanged;
        ProtectPass2Entry.PropertyChanged += OnProtectFieldChanged;
    }

    private void OnProtectFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PasswordBox.Text) && !_loading) _dirty = true;
    }

    public EditNotePage New(string folder)
    {
        Title = AppStrings.Get("NewArticleTitle");
        _tags.Clear();
        _tagsLoaded = true;
        Fill("", folder, "");
        RebuildTagChips();
        return this;
    }

    public EditNotePage Existing(NoteView note, string text, string? passphrase)
    {
        _id = note.Id;
        _passphrase = passphrase;
        Title = AppStrings.Get("EditArticleTitle");
        Fill(note.Title, note.Path, text);
        // An opened protected note shows its password card already on, with the verified passphrase.
        if (passphrase is not null)
        {
            ProtectSwitch.IsToggled = true;
            ProtectFields.IsVisible = true;
            ProtectPassEntry.Text = passphrase;
            ProtectPass2Entry.Text = passphrase;
            ProtectHintEntry.Text = note.ProtectionHint ?? "";
        }
        return this;
    }

    /// <summary>
    /// Loads the tags of an existing note. An idle lock, the background lock or Lock now can close an encrypted database under the read: this
    /// async void catches and stops quietly when the vault is no longer unlocked - the unlock page takes over. Until the
    /// tags are read, a save leaves the note's tags as they are (it never writes an empty list over them). A failure on an open vault is logged
    /// and said in the app's own sentence.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_id is null && string.IsNullOrEmpty(TitleEntry.Text)) TitleEntry.Focus();
        if (_id is not { } id || _tagsLoaded) return;
        var vault = _services.GetRequiredService<FullVault>();
        try
        {
            if (!vault.IsUnlocked) return;
            var tags = await Task.Run(() => _notes.TagsOfAsync(id));
            if (!vault.IsUnlocked) return;
            _tags.Clear();
            _tags.AddRange(tags);
            _tagsLoaded = true;
            RebuildTagChips();
        }
        catch (Exception ex)
        {
            var unlocked = vault.IsUnlocked;
            _services.GetService<ILogger<EditNotePage>>()?.LogWarning(ex, "Reading the tags of a note failed (vault unlocked: {Unlocked})", unlocked);
            if (unlocked) await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    private void Fill(string title, string path, string body)
    {
        _loading = true;
        TitleEntry.Text = title;
        // The root shows its localized name, not a bare slash; Save maps it back.
        PathValue.Text = NotesService.NormalizePath(path) == "/" ? AppStrings.Get("TreeRoot") : path;
        BodyEditor.Text = body;
        _loading = false;
        _dirty = false;
    }

    // ---- tags: the chips with the remove cross, and the add row (as the Android editor) ----

    private void RebuildTagChips()
    {
        TagChips.Children.Clear();
        foreach (var tag in _tags.ToList())
        {
            var pill = new Border
            {
                BackgroundColor = ThemeManager.Token("SurfaceHigh"),
                Stroke = ThemeManager.Token("Border"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(14) },
                Padding = new Thickness(10, 4),
            };
            var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
            row.Children.Add(new Label
            {
                Text = "#" + tag,
                FontSize = 12,
                TextColor = ThemeManager.Token("TextSecondary"),
                VerticalOptions = LayoutOptions.Center,
                VerticalTextAlignment = TextAlignment.Center,
            });
            // The remove target is a Label with a tap, not a Button: a button keeps its own min
            // height and made the whole chip a tall box (the Android editor's lesson).
            var remove = new Label
            {
                Text = "×",
                FontSize = 14,
                TextColor = ThemeManager.Token("TextMuted"),
                Padding = new Thickness(4, 0),
                VerticalOptions = LayoutOptions.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            var captured = tag;
            var removeTap = new TapGestureRecognizer();
            removeTap.Tapped += (_, _) =>
            {
                _tags.Remove(captured);
                RebuildTagChips();
                _dirty = true;
            };
            remove.GestureRecognizers.Add(removeTap);
            row.Children.Add(remove);
            pill.Content = row;
            TagChips.Children.Add(pill);
        }
    }

    private void OnAddTag(object? sender, EventArgs e)
    {
        var tag = (TagEntry.Text ?? "").Trim().TrimStart('#').Trim();
        if (tag.Length > 0 && !_tags.Contains(tag))
        {
            _tags.Add(tag);
            RebuildTagChips();
            _dirty = true;
        }
        TagEntry.Text = "";
    }

    // busy-exempt: opens the folder picker, nothing is sent and nothing waits
    private async void OnChooseFolder(object? sender, EventArgs e)
    {
        var picker = _services.GetRequiredService<FolderPickerPage>();
        await Navigation.PushAsync(picker);
        var path = await picker.ResultTask;
        if (path is null) return;
        PathValue.Text = NotesService.NormalizePath(path) == "/" ? AppStrings.Get("TreeRoot") : path;
        _dirty = true;
    }

    private void OnChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        _flow.Activity();
    }

    // ---- the formatting toolbar: a snippet lands at the cursor (the Android editor) ----

    private void InsertSnippet(string snippet)
    {
        BodyEditor.Text = EditorText.InsertSnippet(BodyEditor.Text ?? "", BodyEditor.CursorPosition, snippet);
        BodyEditor.Focus();
        _dirty = true;
    }

    private void OnToolbarHeading(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetHeading"));
    private void OnToolbarBold(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetBold"));
    private void OnToolbarList(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetList"));
    private void OnToolbarLink(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetLink"));

    // ---- text / preview ----

    private void SetPreviewMode(bool preview)
    {
        if (preview)
        {
            var body = BodyEditor.Text ?? "";
            var markdown = body.Trim().Length == 0 ? AppStrings.Get("EmptyNotePreview") : body;
            PreviewBody.Source = new HtmlWebViewSource { Html = NoteHtml.Render(markdown, ThemeManager.Effective == AppTheme.Dark) };
        }
        ContentEditorShell.IsVisible = !preview;
        EditorToolbar.IsVisible = !preview;
        PreviewBox.IsVisible = preview;
    }

    // ---- the article password card ----

    private void OnProtectToggled(object? sender, ToggledEventArgs e)
    {
        ProtectFields.IsVisible = e.Value;
        if (!_loading) _dirty = true;
    }

    // ---- add a picture from the photo library, through MediaService (the Android editor) ----

    private async void OnAddImage(object? sender, EventArgs e)
    {
        FileResult? photo;
        try
        {
            // One picture at a time, as the Android editor; PickPhotosAsync is the non-obsolete picker.
            var picked = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 });
            photo = picked.FirstOrDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Pick photo failed: " + ex);
            ErrorLabel.Text = AppStrings.Get("PickImageError");
            ErrorLabel.IsVisible = true;
            return;
        }
        if (photo is null) return;

        await BusyAction.RunAsync(sender as Button, async () =>
        {
            using var stream = await photo.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var mediaId = await Task.Run(() => _notes.AddImageAsync(_id, photo.FileName, photo.ContentType, ms.ToArray()));
            BodyEditor.Text = EditorText.InsertImageMarkdown(BodyEditor.Text ?? "", BodyEditor.CursorPosition, mediaId);
            _dirty = true;
        }, new BusyOptions { ResultLabel = ErrorLabel });
    }

    private async void OnSave(object? sender, EventArgs e) => await SaveAsync();

    // The one busy helper (BMB-311): the Save button (also for the toolbar entry and the "Save" of the unsaved-changes question) is off and
    // spins until the note is saved; the whole error is written under it.
    private async Task<bool> SaveAsync()
    {
        ErrorLabel.IsVisible = false;
        ProtectErrorLabel.IsVisible = false;

        // The protection the save should leave the note in; the passphrase is validated here so a
        // too-short or mismatched one is said under the card without spinning or leaving the page.
        ProtectSpec? protect = null;
        if (ProtectSwitch.IsToggled)
        {
            var pass = ProtectPassEntry.Text ?? "";
            var repeat = ProtectPass2Entry.Text ?? "";
            if (pass.Length < 4)
            {
                ProtectErrorLabel.Text = AppStrings.Get("PasswordTooShort");
                ProtectErrorLabel.IsVisible = true;
                return false;
            }
            if (pass != repeat)
            {
                ProtectErrorLabel.Text = AppStrings.Get("PasswordMismatch");
                ProtectErrorLabel.IsVisible = true;
                return false;
            }
            var hint = string.IsNullOrWhiteSpace(ProtectHintEntry.Text) ? null : ProtectHintEntry.Text.Trim();
            protect = new ProtectSpec(true, pass, hint);
        }
        else if (_passphrase is not null)
        {
            // An opened protected note whose switch turned off loses its password.
            protect = new ProtectSpec(false, null, null);
        }

        return await BusyAction.RunAsync(SaveButton, async () =>
        {
            var title = TitleEntry.Text ?? "";
            var path = PathValue.Text ?? "/";
            if (path == AppStrings.Get("TreeRoot")) path = "/";
            var body = BodyEditor.Text ?? "";
            // Tags that could not be read are left as they are (null), never replaced by an empty list.
            var tags = _tagsLoaded ? _tags.ToList() : null;
            _id = await Task.Run(() => _notes.SaveAsync(_id, title, path, body, _passphrase, tags, protect));
            _dirty = false;
            await Navigation.PopAsync();
        }, new BusyOptions { ResultLabel = ErrorLabel });
    }

    private async Task LeaveAsync()
    {
        if (_dirty)
        {
            // The design's three-answer dialog instead of the system action sheet: Save (primary),
            // Don't save (secondary), Cancel (quiet). The dialog pops itself before it answers,
            // so the pop below lands on this page, not on the dialog.
            var choice = await AppDialog.ThreeWayAsync(this, AppStrings.Get("UnsavedTitle"), AppStrings.Get("UnsavedDesc"),
                AppStrings.Get("Save"), AppStrings.Get("DontSave"));
            if (choice == 0)
            {
                await SaveAsync();
                return;
            }
            if (choice != 1) return;
        }
        _dirty = false;
        await Navigation.PopAsync();
    }
}
