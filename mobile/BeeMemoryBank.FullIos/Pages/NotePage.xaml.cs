using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
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
    private bool _showingRaw;
    private List<RelatedItem> _relatedAll = [];
    private int _relatedShown;
    private const int RelatedPageSize = 3;

    public NotePage(IServiceProvider services, NotesService notes)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
        ConceptTagsLayout.ShowAllRequested += OnShowAllTags;
        ConceptTagsLayout.TagTapped += OnTagTapped;
        UpdateMoreIcon();
        SizeChanged += (_, _) => UpdateCommentInputLayout();
        // The native button scales with Dynamic Type but the cross-platform Measure does not, so
        // re-decide once the button has its real laid-out width (and whenever that width changes).
        SendButton.SizeChanged += (_, _) => UpdateCommentInputLayout();
    }

    // The comment field and Send share one line while they fit; when a large system font makes
    // Send too wide to leave the field room for its placeholder, Send drops under the field,
    // full width. Decided from the button's real laid-out width, not a guess at the font size.
    private void UpdateCommentInputLayout()
    {
        if (Width <= 0) return;
        var sendWidth = SendButton.Width > 0 ? SendButton.Width : SendButton.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
        var stack = sendWidth + 8 + 220 > Width - 32;
        if (stack)
        {
            Grid.SetColumnSpan(CommentShell, 2);
            Grid.SetRow(SendButton, 1);
            Grid.SetColumn(SendButton, 0);
            Grid.SetColumnSpan(SendButton, 2);
            SendButton.HorizontalOptions = LayoutOptions.Fill;
        }
        else
        {
            Grid.SetColumnSpan(CommentShell, 1);
            Grid.SetRow(SendButton, 0);
            Grid.SetColumn(SendButton, 1);
            Grid.SetColumnSpan(SendButton, 1);
            SendButton.HorizontalOptions = LayoutOptions.End;
        }
    }

    public NotePage For(Guid id)
    {
        _id = id;
        return this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (Application.Current is { } app) app.RequestedThemeChanged += OnThemeChanged;
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Application.Current is { } app) app.RequestedThemeChanged -= OnThemeChanged;
    }

    // The note's HTML carries the appearance it was rendered with; a theme switch re-renders it
    // and re-colours the path chip (its tokens are picked when it is built).
    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        UpdateMoreIcon();
        if (_note is not null) BuildMetaBar();
        if (_openedText is not null && Body.IsVisible) _ = ShowTextAsync(_openedText);
    }

    // The more button wears the same round circle-ellipsis icon as the tree header, in both
    // appearances (a XAML AppThemeBinding on a ToolbarItem did not pick the dark file on iOS).
    private void UpdateMoreIcon() => MoreItem.IconImageSource = ImageSource.FromFile(ThemeManager.Icon("icon_more"));

    // The meta bar: the path chip (the root with the home icon and its name) and the human date.
    private void BuildMetaBar()
    {
        MetaBar.Children.Clear();
        if (_note is null) return;
        var root = NotesService.NormalizePath(_note.Path) == "/";
        MetaBar.Children.Add(TreeCrumb.Build(root ? Localization.AppStrings.Get("TreeRoot") : _note.Path,
            isCurrent: false, onTap: null, isRoot: root));
        MetaBar.Children.Add(new Label
        {
            Text = ContentFormat.FullDate(_note.UpdatedAt),
            Style = Application.Current?.Resources.TryGetValue("ItemMeta", out var s) == true && s is Style style ? style : null,
            VerticalOptions = LayoutOptions.Center,
        });
    }

    private static bool Dark => ThemeManager.Effective == AppTheme.Dark;

    private async Task LoadAsync()
    {
        try
        {
            _note = await Task.Run(() => _notes.GetAsync(_id));
        }
        catch (Exception ex)
        {
            Show(NoteHtml.Text(Localization.AppStrings.Format("NoteOpenError", ex.Message), Dark));
            return;
        }
        if (_note is null)
        {
            await AppDialog.AlertAsync(this, Localization.AppStrings.Get("ErrorTitle"), Localization.AppStrings.Get("NoteNotFound"));
            await Navigation.PopAsync();
            return;
        }

        Title = _note.Title;
        TitleLabel.Text = _note.Title;
        BuildMetaBar();
        await LoadTagsAsync();
        await LoadCommentsAsync();
        await LoadRelatedAsync();
        // A reload (refresh, return from the editor) starts in the rendered view, never a stale raw one.
        _showingRaw = false;
        RawLabel.IsVisible = false;
        if (!_note.IsProtected)
        {
            _openedText = _note.Body;
            LockedCard.IsVisible = false;
            EditItem.IsEnabled = true;
            await ShowTextAsync(_openedText);
            return;
        }
        if (_passphrase is not null && NotesService.OpenProtected(_note.Body, _passphrase) is { } text)
        {
            _openedText = text;
            LockedCard.IsVisible = false;
            EditItem.IsEnabled = true;
            await ShowTextAsync(text);
            return;
        }
        _openedText = null;
        Body.IsVisible = false;
        RawLabel.IsVisible = false;
        RelatedCard.IsVisible = false;
        LockedCard.IsVisible = true;
        EditItem.IsEnabled = false;
        HintLabel.Text = string.IsNullOrWhiteSpace(_note.ProtectionHint) ? "" : Localization.AppStrings.Format("ProtectedHintFormat", _note.ProtectionHint);
        HintLabel.IsVisible = !string.IsNullOrWhiteSpace(_note.ProtectionHint);
    }

    // ---- related articles: a few at a time, more / all / less, tap opens ----

    private async Task LoadRelatedAsync()
    {
        if (_note is null) return;
        _relatedAll = await Task.Run(() => _notes.RelatedAsync(_note.Id));
        _relatedShown = Math.Min(RelatedPageSize, _relatedAll.Count);
        UpdateRelatedView();
    }

    private void UpdateRelatedView()
    {
        RelatedList.Children.Clear();
        foreach (var r in _relatedAll.Take(_relatedShown))
        {
            var row = new Border
            {
                BackgroundColor = ThemeManager.Token("SurfaceHigh"),
                Stroke = ThemeManager.Token("Border"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12) },
                Padding = new Thickness(12, 10),
                MinimumHeightRequest = 46,
            };
            var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 10 };
            grid.Children.Add(new Image { Source = ThemeManager.Icon("icon_row_file"), WidthRequest = 18, HeightRequest = 18, VerticalOptions = LayoutOptions.Center });
            var title = new Label { Text = r.Title, FontSize = 14, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center, TextColor = ThemeManager.Token("Text") };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            var chev = new Image { Source = ThemeManager.Icon("icon_row_chevron"), WidthRequest = 16, HeightRequest = 16, VerticalOptions = LayoutOptions.Center };
            Grid.SetColumn(chev, 2);
            grid.Children.Add(chev);
            row.Content = grid;
            var captured = r.Id;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await Navigation.PushAsync(_services.GetRequiredService<NotePage>().For(captured));
            row.GestureRecognizers.Add(tap);
            RelatedList.Children.Add(row);
        }
        var total = _relatedAll.Count;
        RelatedHeaderLabel.Text = $"{Localization.AppStrings.Get("RelatedTitle")} ({total})";
        var hasMore = _relatedShown < total;
        RelatedMoreButton.IsVisible = hasMore;
        RelatedAllButton.IsVisible = hasMore;
        RelatedLessButton.IsVisible = _relatedShown > RelatedPageSize;
        RelatedCard.IsVisible = total > 0 && _openedText is not null;
    }

    private void OnRelatedMore(object? sender, EventArgs e)
    {
        _relatedShown = Math.Min(_relatedAll.Count, _relatedShown + RelatedPageSize);
        UpdateRelatedView();
    }

    private void OnRelatedAll(object? sender, EventArgs e)
    {
        _relatedShown = _relatedAll.Count;
        UpdateRelatedView();
    }

    private void OnRelatedLess(object? sender, EventArgs e)
    {
        _relatedShown = Math.Min(RelatedPageSize, _relatedAll.Count);
        UpdateRelatedView();
    }

    // ---- tags: one line with "+N", tap opens the tag's articles ----

    private async Task LoadTagsAsync()
    {
        if (_note is null) return;
        var tags = await Task.Run(() => _notes.TagsOfAsync(_note.Id));
        ConceptTagsLayout.SetTags(tags);
        ConceptTagsLayout.IsVisible = tags.Count > 0;
    }

    private void OnTagTapped(object? sender, string tag) =>
        _ = Navigation.PushAsync(_services.GetRequiredService<TagArticlesPage>().For(tag));

    // The "+N" chip: a sheet with every tag, each chip tappable like the line's.
    private async void OnShowAllTags(object? sender, EventArgs e)
    {
        var tags = ConceptTagsLayout.Tags;
        var wrap = new WrapLayout { Spacing = 8 };
        foreach (var tag in tags)
        {
            var chip = TagLine.CreateChip(tag, new Thickness(0));
            var captured = tag;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                await Navigation.PushAsync(_services.GetRequiredService<TagArticlesPage>().For(captured));
            };
            chip.GestureRecognizers.Add(tap);
            wrap.Children.Add(chip);
        }
        await BottomSheet.ShowAsync(this, wrap, Localization.AppStrings.Format("TagsCountFormat", tags.Count));
    }

    // ---- comments: the list, add and delete through CommentService ----

    private async Task LoadCommentsAsync()
    {
        if (_note is null) return;
        var comments = await Task.Run(() => _notes.CommentsAsync(_note.Id, _passphrase));
        CommentsList.Children.Clear();
        foreach (var c in comments) CommentsList.Children.Add(BuildCommentCard(c));
        NoCommentsLabel.IsVisible = comments.Count == 0;
        CommentsHeaderLabel.Text = Localization.AppStrings.Format("CommentsFormat", comments.Count);
        CommentsCard.IsVisible = true;
    }

    private View BuildCommentCard(CommentView c)
    {
        var text = c.Text ?? Localization.AppStrings.Get("CommentDecryptError");
        var card = new Border
        {
            Style = Application.Current?.Resources.TryGetValue("CardBorder", out var s) == true && s is Style style ? style : null,
            Padding = new Thickness(12),
        };
        var stack = new VerticalStackLayout { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        head.Children.Add(new Label
        {
            // The same human date the note's own meta line shows (today, 10:26).
            Text = ContentFormat.FullDate(c.CreatedAt),
            FontSize = 12,
            TextColor = ThemeManager.Token("TextSecondary"),
        });
        var delete = new Button
        {
            Text = Localization.AppStrings.Get("Delete"),
            FontSize = 12,
            MinimumHeightRequest = 28,
            Padding = new Thickness(6, 2),
            TextColor = ThemeManager.Token("Error"),
            CommandParameter = c,
        };
        if (Application.Current?.Resources.TryGetValue("TextButton", out var ts) == true && ts is Style textStyle) delete.Style = textStyle;
        delete.TextColor = ThemeManager.Token("Error");
        delete.Clicked += OnDeleteComment;
        Grid.SetColumn(delete, 1);
        head.Children.Add(delete);
        stack.Children.Add(head);
        stack.Children.Add(new Label { Text = text, FontSize = 14, LineBreakMode = LineBreakMode.WordWrap });
        card.Content = stack;
        return card;
    }

    private async void OnAddComment(object? sender, EventArgs e)
    {
        if (_note is null) return;
        var text = CommentEntry.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        // Through CommentService: encrypted with the article's key and logged as a sync event.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(() => _notes.AddCommentAsync(_note.Id, text, _passphrase));
            CommentEntry.Text = "";
            await LoadCommentsAsync();
        }, new BusyOptions { FailureTitle = Localization.AppStrings.Get("ErrorTitle") });
    }

    private async void OnDeleteComment(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CommentView comment || _note is null) return;
        if (!await AppDialog.ConfirmAsync(this, Localization.AppStrings.Get("DeleteCommentTitle"),
                Localization.AppStrings.Get("DeleteCommentDesc"), Localization.AppStrings.Get("Delete"), danger: true)) return;
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(() => _notes.DeleteCommentAsync(comment.Id));
            await LoadCommentsAsync();
        }, new BusyOptions { FailureTitle = Localization.AppStrings.Get("ErrorTitle") });
    }

    // The body WebView scrolls with the page, not inside itself: size it to its content once
    // loaded. The first measure can race the layout (large type especially), so measure again
    // shortly after and keep the taller result.
    private async void OnBodyNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (!IsThePageItself(new Uri(e.Url))) return;
        // Navigated fires before pictures decode, so a single measure caught only a strip of the
        // image (real iPhone 11 Pro, 15.11). Keep re-measuring for a couple of seconds and keep the
        // tallest content height, so a late-loading image grows the WebView to its full size.
        for (var i = 0; i < 12; i++)
        {
            MeasureBody();
            await Task.Delay(200);
        }
    }

    private void MeasureBody()
    {
        // The native scroll view's content size, not JavaScript: the WebContent process can
        // refuse JS evaluation under large type, and the content size is what we need anyway.
        if (Body.Handler is Microsoft.Maui.Handlers.WebViewHandler handler)
        {
            var h = handler.PlatformView.ScrollView.ContentSize.Height;
            if (h > 0) Body.HeightRequest = Math.Max(Body.HeightRequest, h + 8);
        }
    }

    // Renders the body, or shows the source text when the more menu's toggle is on. Pictures are
    // embedded as data URIs first (the page's policy loads nothing else); a missing one becomes a line.
    private async Task ShowTextAsync(string markdown)
    {
        LockedCard.IsVisible = false;
        EditItem.IsEnabled = true;
        if (_showingRaw)
        {
            Body.IsVisible = false;
            RawLabel.Text = markdown;
            RawLabel.IsVisible = true;
            return;
        }
        RawLabel.IsVisible = false;
        Body.IsVisible = true;
        var resolved = await Task.Run(() => _notes.ResolveImagesAsync(markdown, Localization.AppStrings.Get("ImageUnavailableFormat")));
        Show(NoteHtml.Render(resolved, Dark));
        UpdateRelatedView();
    }

    private void Show(string html)
    {
        Body.Source = new HtmlWebViewSource { Html = html };
    }

    private async void OnOpenProtected(object? sender, EventArgs e)
    {
        if (_note is null) return;
        var passphrase = PassphraseEntry.Text ?? "";
        // The one busy helper (BMB-311): the button is off and spins through the key derivation; a failure is written under the field.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            // The field is emptied whatever opening ends in (SecretFields): opened, refused, or an exception.
            var text = await SecretFields.RunAsync(() => Task.Run(() => NotesService.OpenProtected(_note.Body, passphrase)), () => PassphraseEntry.Text = "");
            if (text is null)
            {
                PassphraseError.Text = Localization.AppStrings.Get("WrongArticlePassword");
                PassphraseError.IsVisible = true;
                return;
            }
            PassphraseError.IsVisible = false;
            _passphrase = passphrase;
            _openedText = text;
            _showingRaw = false;
            await ShowTextAsync(text);
            // The comments were loaded locked; open them with the passphrase just verified.
            await LoadCommentsAsync();
        }, new BusyOptions { ResultLabel = PassphraseError });
    }

    // ---- the more menu: move, source/rendered, password, delete (the app's own sheet) ----

    // busy-exempt: opens the note's own sheet; each action in it runs behind BusyAction there
    private async void OnMore(object? sender, EventArgs e) => await ShowMoreMenuAsync();

#if DEBUG || BMB_E2E
    /// <summary>The end-to-end check photographs the note's more menu.</summary>
    internal Task ShowMoreMenuForE2EAsync() => ShowMoreMenuAsync();

    /// <summary>The end-to-end check photographs the source view.</summary>
    internal Task ShowRawForE2EAsync()
    {
        _showingRaw = true;
        return _openedText is not null ? ShowTextAsync(_openedText) : Task.CompletedTask;
    }

    /// <summary>The end-to-end check photographs the comment input (scrolls it into view).</summary>
    internal async Task ScrollToCommentsForE2EAsync()
    {
        await Task.Delay(300);
        await Scroller.ScrollToAsync(CommentsCard, ScrollToPosition.End, false);
    }
#endif

    internal async Task ShowMoreMenuAsync()
    {
        if (_note is null) return;
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        var sheet = new BottomSheet.BottomSheetPage(stack, _note.Title);

        void Add(string text, string styleKey, Func<Task> act)
        {
            var button = new Button { Text = text, HorizontalOptions = LayoutOptions.Fill };
            if (resources is not null && resources.TryGetValue(styleKey, out var s) && s is Style style) button.Style = style;
            button.Clicked += async (_, _) =>
            {
                await sheet.CloseAsync();
                await act();
            };
            stack.Children.Add(button);
        }

        Add(Localization.AppStrings.Get("Move"), "SecondaryButton", MoveNoteAsync);
        if (_openedText is not null)
            Add(_showingRaw ? Localization.AppStrings.Get("ShowRendered") : Localization.AppStrings.Get("ShowRaw"),
                "SecondaryButton", () => { ToggleRaw(); return Task.CompletedTask; });
        Add(_note.IsProtected ? Localization.AppStrings.Get("RemovePassword") : Localization.AppStrings.Get("SetPassword"),
            "SecondaryButton", PasswordAsync);
        Add(Localization.AppStrings.Get("Delete"), "DangerButton", DeleteNoteAsync);

        var cancel = new Button { Text = Localization.AppStrings.Get("Cancel"), HorizontalOptions = LayoutOptions.Fill };
        if (resources is not null && resources.TryGetValue("TextButton", out var ts) == true && ts is Style textStyle) cancel.Style = textStyle;
        cancel.Clicked += async (_, _) => await sheet.CloseAsync();
        stack.Children.Add(cancel);

        await Navigation.PushModalAsync(sheet);
    }

    private void ToggleRaw()
    {
        if (_openedText is null) return;
        _showingRaw = !_showingRaw;
        _ = ShowTextAsync(_openedText);
    }

    private async Task MoveNoteAsync()
    {
        if (_note is null) return;
        var picker = _services.GetRequiredService<FolderPickerPage>().ForMove(null);
        await Navigation.PushAsync(picker);
        var destination = await picker.ResultTask;
        if (destination is null) return;
        await BusyAction.RunAsync(null, async () =>
        {
            await _notes.EnsureFolderAsync(destination);
            await _notes.MoveArticleAsync(_note.Id, destination);
            await LoadAsync();
        }, new BusyOptions { FailureTitle = Localization.AppStrings.Get("ErrorTitle") });
    }

    private async Task PasswordAsync()
    {
        if (_note is null) return;
        if (!_note.IsProtected)
        {
            // A plain note gains its password in the editor (as Android's note menu).
            if (_openedText is not null)
                await Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().Existing(_note, _openedText, _passphrase));
            return;
        }
        var pass = await this.ShowInputPopupAsync(Localization.AppStrings.Get("RemovePasswordTitle"), Localization.AppStrings.Get("RemovePasswordDesc"),
            Localization.AppStrings.Get("Remove"), Localization.AppStrings.Get("Cancel"),
            placeholder: Localization.AppStrings.Get("ArticlePasswordPlaceholder"), isPassword: true);
        if (string.IsNullOrEmpty(pass)) return;
        await BusyAction.RunAsync(null, async () =>
        {
            try
            {
                await Task.Run(() => _notes.UnprotectAsync(_note.Id, pass));
                _passphrase = null;
                await LoadAsync();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                await AppDialog.AlertAsync(this, Localization.AppStrings.Get("WrongPasswordTitle"), Localization.AppStrings.Get("WrongPasswordDesc"));
            }
        }, new BusyOptions { FailureTitle = Localization.AppStrings.Get("ErrorTitle") });
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // A pull asks for a sync round then reloads, like the tree (the pull's own spinner, no pop-up).
        try
        {
            await BusyAction.RunAsync(Refresher, async () =>
            {
                await _services.GetRequiredService<FullSync>().RoundAsync();
                await LoadAsync();
            }, new BusyOptions { FailureTitle = Localization.AppStrings.Get("TabSync") });
        }
        finally
        {
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        // The note's own page (an HTML string: about:blank or the app's file base) loads; nothing else ever does inside the view. A link
        // the person tapped is cancelled here and, for web and mail links, offered to the system after a question.
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var target) && IsThePageItself(target)) return;
        e.Cancel = true;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto")) return;
        if (await AppDialog.ConfirmAsync(this, Localization.AppStrings.Get("OpenLinkTitle"), uri.ToString(), Localization.AppStrings.Get("OpenLinkAction")))
            await Launcher.Default.OpenAsync(uri);
    }

    /// <summary>about:blank, or the app bundle the HTML string is loaded against - never another file, never the network.</summary>
    private static bool IsThePageItself(Uri uri) =>
        uri.Scheme == "about" || (uri.IsFile && uri.AbsolutePath.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal));

    // busy-exempt: opens the editor, nothing is sent and nothing waits
    private async void OnEdit(object? sender, EventArgs e)
    {
        if (_note is null || _openedText is null) return;
        await Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().Existing(_note, _openedText, _passphrase));
    }

    private async Task DeleteNoteAsync()
    {
        if (_note is null) return;
        if (!await AppDialog.ConfirmAsync(this, Localization.AppStrings.Get("DeleteArticleTitle"), Localization.AppStrings.Get("DeleteArticleDesc"),
                Localization.AppStrings.Get("Delete"), danger: true))
            return;
        // The one busy helper (BMB-311): the entry is off until the note is in the trash; a failure is said in full.
        await BusyAction.RunAsync(null, async () =>
        {
            await _notes.DeleteAsync(_note.Id);
            await Navigation.PopAsync();
        }, new BusyOptions { FailureTitle = "Move to trash" });
    }
}
