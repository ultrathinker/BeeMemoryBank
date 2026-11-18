using System.Text.RegularExpressions;
using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Indiko.Maui.Controls.Markdown.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(ArticleId), "id")]
[QueryProperty(nameof(InitialPath), "path")]
public partial class ArticleEditPage : ContentPage
{
    private readonly IServiceProvider _services;
    private Guid? _articleId;
    // Editing an existing protected article: the passphrase (from the read→edit handoff or the edit
    // gate) is held in memory and used to re-wrap the body on save. _protectedBlobForEdit holds the
    // BMBENC1 body while the gate is up so Unlock can decrypt it.
    private bool _isProtectedArticle;
    private string? _editPassphrase;
    private string? _protectedBlobForEdit;
    // Guards Save against the race where the user taps Save before LoadAsync has discovered the
    // article is protected (which would otherwise wipe the BMBENC1 body via the plaintext path).
    // New articles never call LoadAsync, so they start ready.
    private bool _loaded = true;

    private readonly List<string> _tags = [];
    private string _initialSnapshot = "";

    /// <summary>Set once the way out is confirmed (saved or discarded), so the shell lets the navigation through.</summary>
    internal bool LeaveConfirmed { get; set; }

    public string ArticleId
    {
        set
        {
            if (Guid.TryParse(value, out var id))
            {
                _articleId = id;
                Title = AppStrings.Get("EditArticleTitle");
                _loaded = false; // block Save until the article (and its protected state) is loaded
                _ = LoadAsync(id);
            }
        }
    }

    public string InitialPath
    {
        set
        {
            if (!string.IsNullOrEmpty(value))
                PathLabel.Text = Uri.UnescapeDataString(value);
        }
    }

    public ArticleEditPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        Title = AppStrings.Get("NewArticleTitle");
        PathLabel.Text = "/";
        // The password card is offered for new and existing articles alike (the mock-up's editor).
        ProtectSection.IsVisible = true;
        FolderPickerPage.FolderSelected = path => PathLabel.Text = path;

        ModeSegments.Items = [AppStrings.Get("ModeText"), AppStrings.Get("ModePreview")];
        ModeSegments.SelectedIndex = 0;
        ModeSegments.SelectionChanged += (_, index) => SetPreviewMode(index == 1);

        ApplyMarkdownTheme();
        if (Application.Current is { } app) app.RequestedThemeChanged += OnRequestedThemeChanged;
        TakeSnapshot();
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => ApplyMarkdownTheme();

    private void ApplyMarkdownTheme()
    {
        PreviewMarkdown.Theme = ThemeManager.Effective == AppTheme.Light ? MarkdownThemeDefaults.Light : MarkdownThemeDefaults.Dark;
        PreviewMarkdown.ApplyAppFonts();
    }

    private void OnProtectToggled(object? sender, ToggledEventArgs e)
    {
        ProtectFields.IsVisible = e.Value;
    }

    // ── Tags: the chips with the remove cross, and the add row ───────────────

    private void RebuildTagChips()
    {
        TagChipsLayout.Children.Clear();
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
            string? chipFont = null;
            if (Application.Current?.Resources.TryGetValue("FontBodySemibold", out var fontRes) == true)
                chipFont = fontRes as string;
            row.Children.Add(new Label
            {
                Text = "#" + tag,
                FontSize = 12,
                FontFamily = chipFont,
                TextColor = ThemeManager.Token("TextSecondary"),
                VerticalOptions = LayoutOptions.Center,
                VerticalTextAlignment = TextAlignment.Center,
            });
            // The remove target is a Label with a tap, not a Button: a MaterialButton keeps its own
            // min height and insets and made the whole chip a tall box. A Label keeps the pill the
            // same height as the article's TagLine chip.
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
            };
            remove.GestureRecognizers.Add(removeTap);
            row.Children.Add(remove);
            pill.Content = row;
            TagChipsLayout.Children.Add(pill);
        }
    }

    private void OnAddTagClicked(object? sender, EventArgs e)
    {
        var tag = (ConceptTagsEntry.Text ?? "").Trim().TrimStart('#').Trim();
        if (tag.Length > 0 && !_tags.Contains(tag))
        {
            _tags.Add(tag);
            RebuildTagChips();
        }
        ConceptTagsEntry.Text = "";
    }

    // ── Text / Preview and the formatting panel ──────────────────────────────

    private void SetPreviewMode(bool preview)
    {
        if (preview)
        {
            var body = ContentEditor.Text ?? "";
            PreviewMarkdown.MarkdownText = ContentFormat.SanitizeMarkdown(
                body.Trim().Length == 0 ? AppStrings.Get("EmptyNotePreview") : body);
        }
        ContentEditorShell.IsVisible = !preview;
        EditorToolbar.IsVisible = !preview;
        PreviewBox.IsVisible = preview;
    }

    private void InsertSnippet(string snippet)
    {
        var text = ContentEditor.Text ?? "";
        var cursor = Math.Clamp(ContentEditor.CursorPosition, 0, text.Length);
        ContentEditor.Text = text.Insert(cursor, snippet);
        ContentEditor.Focus();
    }

    private void OnToolbarHeadingClicked(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetHeading"));
    private void OnToolbarBoldClicked(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetBold"));
    private void OnToolbarListClicked(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetList"));
    private void OnToolbarLinkClicked(object? sender, EventArgs e) => InsertSnippet(AppStrings.Get("SnippetLink"));

    // ── The edit gate of an existing protected article ───────────────────────

    private async void OnEditUnlockClicked(object? sender, EventArgs e)
    {
        var pass = EditUnlockPassEntry.Text ?? "";
        if (pass.Length == 0) { EditUnlockPassEntry.Focus(); return; }
        if (_protectedBlobForEdit == null) return;

        EditUnlockErrorLabel.IsVisible = false;
        var blob = _protectedBlobForEdit;
        // The one busy helper (BMB-311): the button is off and spins through the key derivation; a failure other than a wrong password is
        // said in full in the error line.
        await BusyAction.RunAsync(sender as Button, () => EditUnlockAsync(blob, pass), new BusyOptions { ResultLabel = EditUnlockErrorLabel });
    }

    private async Task EditUnlockAsync(string blob, string pass)
    {
        string pt;
        try
        {
            pt = await Task.Run(() => BeeMemoryBank.Crypto.ProtectedContentCodec.Unwrap(blob, pass));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            EditUnlockErrorLabel.Text = AppStrings.Get("WrongArticlePassword");
            EditUnlockErrorLabel.IsVisible = true;
            return;
        }

        _editPassphrase = pass;
        EditUnlockPassEntry.Text = "";
        ContentEditor.Text = pt;
        EditUnlockGate.IsVisible = false;
        ContentEditorShell.IsVisible = true;
        FillProtectionFields(pass, _protectionHint);
        TakeSnapshot();
    }

    private string? _protectionHint;

    /// <summary>The password card of an existing protected article: the verified passphrase and the hint.</summary>
    private void FillProtectionFields(string passphrase, string? hint)
    {
        ProtectSwitch.IsToggled = true;
        ProtectFields.IsVisible = true;
        ProtectPassEntry.Text = passphrase;
        ProtectPass2Entry.Text = passphrase;
        ProtectHintEntry.Text = hint ?? "";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_articleId == null && ShareIntentHandler.PendingText != null)
        {
            ContentEditor.Text = ShareIntentHandler.PendingText;
            ShareIntentHandler.PendingText = null;
            TakeSnapshot();
        }
    }

    private async Task LoadAsync(Guid id)
    {
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;
        // Reset protected state up-front so a reused page can never carry stale flags into a save.
        _isProtectedArticle = false;
        _editPassphrase = null;
        _protectedBlobForEdit = null;
        _protectionHint = null;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var conceptTagSvc = scope.ServiceProvider.GetRequiredService<ConceptTagService>();

            var article = await articleSvc.GetMetadataAsync(id);
            if (article == null) return;

            TitleEntry.Text = article.Title;
            PathLabel.Text = article.TreePath;
            _protectionHint = article.ProtectionHint;

            var conceptTags = await conceptTagSvc.GetByArticleIdAsync(id);
            _tags.Clear();
            _tags.AddRange(conceptTags);
            RebuildTagChips();

            var content = await articleSvc.GetContentAsync(id);

            if (article.Protected)
            {
                _isProtectedArticle = true;
                _protectedBlobForEdit = content; // BMBENC1 body; decrypted after unlock/handoff
                ProtectSwitch.IsToggled = true;
                ProtectFields.IsVisible = true;

                // Try the read→edit handoff first (passphrase verified moments ago on the detail page).
                var handoff = _services.GetService<MobileUnlockHolder>()?.Take(id);
                if (handoff != null)
                {
                    try
                    {
                        var pt = await Task.Run(() => BeeMemoryBank.Crypto.ProtectedContentCodec.Unwrap(_protectedBlobForEdit, handoff));
                        _editPassphrase = handoff;
                        ContentEditor.Text = pt;
                        FillProtectionFields(handoff, _protectionHint);
                        return; // unlocked via handoff — no gate needed
                    }
                    catch { /* stale handoff — fall through to the gate */ }
                }

                // No (valid) handoff → show the unlock gate; the editor stays hidden until unlocked.
                EditUnlockGate.IsVisible = true;
                ContentEditorShell.IsVisible = false;
                return;
            }

            ContentEditor.Text = content;
        }
        catch (Exception ex)
        {
            ShowError(GetErrorMessage(ex));
        }
        finally
        {
            _loaded = true; // protected state is now known — Save is safe
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            TakeSnapshot();
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private async void OnSaveClicked(object? sender, EventArgs e) => await TrySaveAsync(sender as BindableObject);

    private async Task TrySaveAsync(BindableObject? target)
    {
        if (!_loaded)
        {
            ShowError(AppStrings.Get("StillLoading"));
            return;
        }
        var title = TitleEntry.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            TitleErrorLabel.Text = AppStrings.Get("TitleRequired");
            TitleErrorLabel.IsVisible = true;
            return;
        }
        TitleErrorLabel.IsVisible = false;

        var path = PathLabel.Text?.Trim() ?? "/";
        if (!path.StartsWith('/')) path = "/" + path;

        var content = ContentEditor.Text ?? "";

        ErrorLabel.IsVisible = false;
        ProtectErrorLabel.IsVisible = false;
        // The one busy helper (BMB-311): the button (or the toolbar entry) is off and spins until the article is saved and the page is left.
        await BusyAction.RunAsync(target, () => SaveAsync(title, path, _tags.ToList(), content));
    }

    private async Task SaveAsync(string title, string path, List<string> conceptTags, string content)
    {
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var commentSvc = scope.ServiceProvider.GetRequiredService<CommentService>();

            if (_articleId.HasValue)
            {
                var id = _articleId.Value;
                if (_isProtectedArticle)
                {
                    if (_editPassphrase == null) { ShowProtectError(AppStrings.Get("UnlockFirst")); return; }

                    if (ProtectSwitch.IsToggled)
                    {
                        var newPass = ProtectPassEntry.Text ?? "";
                        var repeat = ProtectPass2Entry.Text ?? "";
                        var hint = string.IsNullOrWhiteSpace(ProtectHintEntry.Text) ? null : ProtectHintEntry.Text.Trim();
                        if (newPass.Length < 4) { ShowProtectError(AppStrings.Get("PasswordTooShort")); return; }
                        if (newPass != repeat) { ShowProtectError(AppStrings.Get("PasswordMismatch")); return; }

                        if (newPass != _editPassphrase)
                        {
                            // A new passphrase: the core re-wraps the body and the hint; the comments
                            // sealed under the old one are re-sealed under the new one too.
                            await articleSvc.ChangePassphraseAsync(id, _editPassphrase, newPass, hint);
                            await commentSvc.ReprotectAsync(id, _editPassphrase, newPass);
                            _editPassphrase = newPass;
                            _services.GetService<MobileUnlockHolder>()?.Clear();
                        }
                        // Re-wrap the new body under the passphrase (verifies it first).
                        await articleSvc.UpdateProtectedContentAsync(id, content, _editPassphrase);
                        // Metadata + tags + hint only (no body — keeps it protected).
                        await articleSvc.UpdateAsync(id, title, path, conceptTags, null, hint, updateHint: true);
                    }
                    else
                    {
                        // The switch turned off: take the protection off the body and the comments.
                        await articleSvc.UnprotectAsync(id, _editPassphrase);
                        await commentSvc.ReprotectAsync(id, _editPassphrase, null);
                        await articleSvc.UpdateAsync(id, title, path, conceptTags, content);
                        _isProtectedArticle = false;
                        _editPassphrase = null;
                        _services.GetService<MobileUnlockHolder>()?.Clear();
                    }
                }
                else if (ProtectSwitch.IsToggled)
                {
                    // A plain article gains a password: save the text, then seal it (and the comments).
                    var pass = ProtectPassEntry.Text ?? "";
                    var repeat = ProtectPass2Entry.Text ?? "";
                    var hint = string.IsNullOrWhiteSpace(ProtectHintEntry.Text) ? null : ProtectHintEntry.Text.Trim();
                    if (pass.Length < 4) { ShowProtectError(AppStrings.Get("PasswordTooShort")); return; }
                    if (pass != repeat) { ShowProtectError(AppStrings.Get("PasswordMismatch")); return; }

                    await articleSvc.UpdateAsync(id, title, path, conceptTags, content);
                    await articleSvc.ProtectAsync(id, pass, hint);
                    await commentSvc.ReprotectAsync(id, null, pass);
                }
                else
                {
                    await articleSvc.UpdateAsync(id, title, path, conceptTags, content);
                }
                LeaveConfirmed = true;
                await Shell.Current.GoToAsync($"..?created={id}");
            }
            else
            {
                string? hint = null;
                if (ProtectSwitch.IsToggled)
                {
                    var pass = ProtectPassEntry.Text ?? "";
                    if (pass.Length < 4) { ShowProtectError(AppStrings.Get("PasswordTooShort")); return; }
                    if (pass != (ProtectPass2Entry.Text ?? "")) { ShowProtectError(AppStrings.Get("PasswordMismatch")); return; }
                    // Wrap locally so the very first CREATE event carries only ciphertext.
                    content = BeeMemoryBank.Crypto.ProtectedContentCodec.Wrap(content, pass);
                    hint = string.IsNullOrWhiteSpace(ProtectHintEntry.Text) ? null : ProtectHintEntry.Text.Trim();
                }
                var article = await articleSvc.CreateAsync(title, path, conceptTags, content, hint);
                LeaveConfirmed = true;
                await Shell.Current.GoToAsync($"..?created={article.Id}");
            }
        }
        catch (Exception ex)
        {
            ShowError(GetErrorMessage(ex));
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

    private void ShowProtectError(string message)
    {
        ProtectErrorLabel.Text = message;
        ProtectErrorLabel.IsVisible = true;
    }

    private static string GetErrorMessage(Exception ex)
    {
        // Unwrap TargetInvocationException and AggregateException to show the real error
        var inner = ex;
        while (inner is System.Reflection.TargetInvocationException or AggregateException)
            inner = inner.InnerException ?? inner;
        if (inner == ex) return ex.Message;

        System.Diagnostics.Debug.WriteLine($"[BeeMemoryBank] {ex.GetType().Name}: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"[BeeMemoryBank] Inner: {inner.GetType().Name}: {inner.Message}");
        System.Diagnostics.Debug.WriteLine($"[BeeMemoryBank] StackTrace: {inner.StackTrace}");

        return $"{inner.GetType().Name}: {inner.Message}";
    }

    // ── Unsaved changes (the mock-up's three-answer dialog on the way out) ───

    private string CurrentSnapshot() => string.Join("\u001f",
        TitleEntry.Text ?? "", PathLabel.Text ?? "", string.Join(",", _tags),
        ContentEditor.Text ?? "", ProtectSwitch.IsToggled ? "1" : "0",
        ProtectPassEntry.Text ?? "", ProtectPass2Entry.Text ?? "", ProtectHintEntry.Text ?? "");

    private void TakeSnapshot() => _initialSnapshot = CurrentSnapshot();

    private bool IsDirty() => _loaded && CurrentSnapshot() != _initialSnapshot;

    protected override bool OnBackButtonPressed()
    {
        if (IsDirty())
        {
            _ = HandleUnsavedAsync();
            return true;
        }
        return false;
    }

    // busy-exempt: the dialog itself only reads the fields; saving runs behind BusyAction in TrySaveAsync
    private async Task HandleUnsavedAsync()
    {
        if (await RequestLeaveAsync())
        {
            LeaveConfirmed = true;
            await Shell.Current.GoToAsync("..");
        }
    }

    /// <summary>
    /// The unsaved-changes question, also asked by the shell for the toolbar up button (Shell's up
    /// navigation does not go through OnBackButtonPressed). True when the page may be left now;
    /// saving navigates on its own, so it answers false.
    /// </summary>
    internal async Task<bool> RequestLeaveAsync()
    {
        if (!IsDirty()) return true;
        var choice = await AppDialog.ThreeWayAsync(this,
            AppStrings.Get("UnsavedTitle"), AppStrings.Get("UnsavedDesc"),
            AppStrings.Get("Save"), AppStrings.Get("DontSave"), AppStrings.Get("Cancel"));
        if (choice == 0)
        {
            await TrySaveAsync(null);
            return false;
        }
        return choice == 1;
    }

    // ── Images ───────────────────────────────────────────────────────────────

    private async void OnAddImageClicked(object? sender, EventArgs e)
    {
        var action = await DisplayActionSheet(AppStrings.Get("AddImageTitle"), AppStrings.Get("Cancel"), null,
            AppStrings.Get("PhotoLibrary"), AppStrings.Get("TakePhoto"));
        if (action == AppStrings.Get("Cancel") || action == null) return;

        FileResult? photo;
        try
        {
            photo = action == AppStrings.Get("TakePhoto")
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await MediaPicker.Default.PickPhotoAsync();
        }
        catch (Exception ex)
        {
            ShowError(GetErrorMessage(ex));
            return;
        }
        if (photo == null) return;

        // The one busy helper (BMB-311): the button is off and spins while the picture is read, encrypted and stored; a failure is said in
        // full in the error line.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            using var stream = await photo.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var plaintext = ms.ToArray();

            using var scope = _services.CreateScope();
            var mediaService = scope.ServiceProvider.GetRequiredService<MediaService>();

            var media = await mediaService.CreateAsync(
                photo.FileName, photo.ContentType, plaintext, _articleId);

            // Generate image-NNN alt text
            var content = ContentEditor.Text ?? "";
            int counter = 0;
            foreach (Match m in Regex.Matches(content, @"!\[image-(\d+)\]"))
            {
                if (int.TryParse(m.Groups[1].Value, out var n) && n >= counter)
                    counter = n + 1;
            }
            var altText = $"image-{counter.ToString().PadLeft(3, '0')}";

            var markdown = $"![{altText}](/api/media/{media.Id})";

            // Insert at cursor position
            var cursorPos = ContentEditor.CursorPosition;
            var text = ContentEditor.Text ?? "";
            ContentEditor.Text = text.Insert(cursorPos, "\n" + markdown + "\n");
        }, new BusyOptions { Result = (ok, text) => { if (!ok) ShowError(text); } });
    }

    // busy-exempt: opens the folder picker, nothing is sent and nothing waits
    private async void OnChooseFolderClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("folderPicker");
    }
}
