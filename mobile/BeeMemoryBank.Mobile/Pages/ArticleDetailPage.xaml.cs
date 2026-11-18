using System.Text.RegularExpressions;
using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
using Indiko.Maui.Controls.Markdown.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Layouts;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(ArticleId), "id")]
public partial class ArticleDetailPage : ContentPage
{
    private readonly IServiceProvider _services;
    private Guid _parsedId;
    private string _rawContent = "";
    private bool _showingRaw = false;
    // A protected article's body is a passphrase-encrypted BMBENC1 blob: it is unwrapped
    // on-device after the passphrase is entered, never edited as ciphertext.
    private bool _isProtected = false;
    private string? _protectedBlob; // the BMBENC1 body; decrypted locally after the user enters the passphrase
    // The passphrase verified by this page's unlock; opens and seals the protected article's comments.
    private string? _unlockedPassphrase;
    private CancellationTokenSource? _cts;
    private List<RelatedArticle> _relatedAll = new();
    private int _relatedShown;
    private const int RelatedPageSize = 3;

    public string ArticleId
    {
        set
        {
            // Use a temp: Guid.TryParse assigns Guid.Empty to its out param on failure. Binding
            // directly to _parsedId meant a back-navigation that re-applies the query WITHOUT `id`
            // (e.g. "..?created=X") wiped _parsedId to Empty → every later reload queried an empty
            // GUID → "article not found". Only update _parsedId on a successful parse.
            if (Guid.TryParse(value, out var newId))
            {
                _parsedId = newId;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _ = LoadAsync(_parsedId, _cts.Token);
            }
        }
    }

    public ArticleDetailPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        // Native markdown rendering (no WebView): avoids the hardware-accelerated WebView GL
        // crash on old GPUs, the blank-render in a ScrollView, and the auto-height problems.
        ApplyMarkdownTheme();
        if (Application.Current is { } app) app.RequestedThemeChanged += OnRequestedThemeChanged;
        ConceptTagsLayout.ShowAllRequested += OnShowAllTagsRequested;
    }

    // The "+N" chip of the one-line tag row: a bottom sheet with every tag as wrapping chips, titled
    // with the tag count. A chip in the sheet behaves like a chip in the line (it opens nothing).
    private async void OnShowAllTagsRequested(object? sender, EventArgs e)
    {
        var tags = ConceptTagsLayout.Tags;
        if (tags.Count == 0) return;
        // A wrap layout of our own measures each chip at its whole width and wraps whole chips, so the
        // first row is never cut into "#alph" the way a FlexLayout squeezed it. No FlexLayout here.
        var wrap = new WrapLayout { Spacing = 8 };
        foreach (var tag in tags)
            wrap.Children.Add(TagLine.CreateChip(tag, new Thickness(0)));
        await BottomSheet.ShowAsync(this, wrap, AppStrings.Format("TagsCountFormat", tags.Count));
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => ApplyMarkdownTheme();

    private void ApplyMarkdownTheme()
    {
        ContentMarkdown.Theme = ThemeManager.Effective == AppTheme.Light ? MarkdownThemeDefaults.Light : MarkdownThemeDefaults.Dark;
        ContentMarkdown.ApplyAppFonts();
    }

    private bool _appearedBefore;

    private async Task LoadAsync(Guid id, CancellationToken ct, bool isInitial = true)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        TitleLabel.IsVisible = false;
        ConceptTagsLayout.IsVisible = false;
        MetaCard.IsVisible = false;
        ContentCard.IsVisible = false;
        RelatedArticlesCard.IsVisible = false;
        CommentsCard.IsVisible = false;
        // Reset protected state so a reused/refreshed page can't keep a stale lock card, a stale
        // _isProtected (which would wrongly block Edit), or a lingering decrypted body in memory.
        LockCard.IsVisible = false;
        LockHintLabel.IsVisible = false;
        UnlockErrorLabel.IsVisible = false;
        _isProtected = false;
        _protectedBlob = null;
        _unlockedPassphrase = null;
        _rawContent = "";

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var conceptTagSvc = scope.ServiceProvider.GetRequiredService<ConceptTagService>();

            var article = await articleSvc.GetMetadataAsync(id);
            if (ct.IsCancellationRequested) return;

            if (article == null)
            {
                // On a reload/refresh (e.g. mid-sync) GetMetadataAsync can transiently return null.
                // Retry once after a short gap to tell a transient sync window apart from a real
                // deletion before deciding to kick the user back to the list.
                if (!isInitial)
                {
                    await Task.Delay(400, ct);
                    if (ct.IsCancellationRequested) return;
                    article = await articleSvc.GetMetadataAsync(id);
                }
            }
            if (article == null)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ArticleNotFound"));
                await Shell.Current.GoToAsync("..");
                return;
            }

            Title = article.Title;
            TitleLabel.Text = article.Title;
            PathLabel.Text = article.TreePath;
            UpdatedLabel.Text = ContentFormat.FullDate(article.UpdatedAt);

            TitleLabel.IsVisible = true;

            var conceptTags = await conceptTagSvc.GetByArticleIdAsync(id);
            if (ct.IsCancellationRequested) return;
            if (conceptTags.Count > 0)
            {
                ConceptTagsLayout.SetTags(conceptTags);
                ConceptTagsLayout.IsVisible = true;
            }

            MetaCard.IsVisible = true;

            var content = await articleSvc.GetContentAsync(id);
            if (ct.IsCancellationRequested) return;

            if (article.Protected || BeeMemoryBank.Crypto.ProtectedContentCodec.IsProtected(content))
            {
                _isProtected = true;
                _protectedBlob = content; // keep the encrypted blob; decrypted locally after Unlock
                if (!string.IsNullOrWhiteSpace(article.ProtectionHint))
                {
                    LockHintLabel.Text = AppStrings.Format("ProtectedHintFormat", article.ProtectionHint);
                    LockHintLabel.IsVisible = true;
                }
                else
                {
                    LockHintLabel.Text = AppStrings.Get("EnterArticlePassword");
                    LockHintLabel.IsVisible = true;
                }
                // Show the unlock card; the content stays hidden until the passphrase is entered.
                LockCard.IsVisible = true;
                PasswordItem.Text = AppStrings.Get("RemovePassword");
            }
            else
            {
                PasswordItem.Text = AppStrings.Get("SetPassword");
                _rawContent = content;

                var mediaService = scope.ServiceProvider.GetService<MediaService>();
                if (mediaService != null)
                    content = await ResolveMediaImagesAsync(content, mediaService);

                if (ct.IsCancellationRequested) return;

                RenderContent(content);
                ContentCard.IsVisible = true;
            }

            var related = await conceptTagSvc.GetRelatedArticlesAsync(id);
            if (ct.IsCancellationRequested) return;
            if (related.Count > 0)
            {
                _relatedAll = related;
                _relatedShown = Math.Min(RelatedPageSize, related.Count);
                UpdateRelatedView();
                RelatedArticlesCard.IsVisible = true;
            }

            await LoadCommentsAsync(id, scope.ServiceProvider);
            CommentsCard.IsVisible = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RenderContent($"**{AppStrings.Get("ErrorLoadingContent")}:** {ex.Message}");
            ContentCard.IsVisible = true;
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    // Related articles: show a few at a time with paging + "show all", and surface the count.
    private void UpdateRelatedView()
    {
        int total = _relatedAll.Count;
        RelatedHeaderLabel.Text = $"{AppStrings.Get("RelatedTitle")} ({total})";
        BindableLayout.SetItemsSource(RelatedArticlesList, _relatedAll.Take(_relatedShown).ToList());

        bool hasMore = _relatedShown < total;
        RelatedShowMoreButton.IsVisible = hasMore;
        RelatedShowAllButton.IsVisible = hasMore;
        RelatedShowLessButton.IsVisible = _relatedShown > RelatedPageSize;
    }

    private void OnRelatedShowMoreClicked(object? sender, EventArgs e)
    {
        _relatedShown = Math.Min(_relatedAll.Count, _relatedShown + RelatedPageSize);
        UpdateRelatedView();
    }

    private void OnRelatedShowAllClicked(object? sender, EventArgs e)
    {
        _relatedShown = _relatedAll.Count;
        UpdateRelatedView();
    }

    private void OnRelatedShowLessClicked(object? sender, EventArgs e)
    {
        _relatedShown = Math.Min(RelatedPageSize, _relatedAll.Count);
        UpdateRelatedView();
    }

    // busy-exempt: opens the related article, nothing is sent and nothing waits
    private async void OnRelatedArticleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is Guid relatedId)
        {
            await Shell.Current.GoToAsync($"articleDetail?id={relatedId}");
        }
    }

    // busy-exempt: opens the folder of the article in the tree, nothing is sent
    private async void OnPathChipTapped(object? sender, TappedEventArgs e)
    {
        var path = PathLabel.Text;
        if (string.IsNullOrEmpty(path) || path == "/")
            await Shell.Current.GoToAsync("//tree");
        else
            await Shell.Current.GoToAsync($"treeFolder?path={Uri.EscapeDataString(path)}");
    }

    private void OnToggleViewClicked(object? sender, EventArgs e)
    {
        _showingRaw = !_showingRaw;
        if (_showingRaw)
        {
            ContentMarkdown.IsVisible = false;
            ContentRawLabel.Text = _rawContent;
            ContentRawLabel.IsVisible = true;
            ToggleViewItem.Text = AppStrings.Get("ShowRendered");
        }
        else
        {
            ContentRawLabel.IsVisible = false;
            ContentMarkdown.IsVisible = true;
            ToggleViewItem.Text = AppStrings.Get("ShowRaw");
        }
    }

    private static async Task<string> ResolveMediaImagesAsync(string markdown, MediaService mediaService)
    {
        var pattern = new Regex(@"!\[([^\]]*)\]\(/api/media/([0-9a-f\-]{36})\)");
        var matches = pattern.Matches(markdown);
        if (matches.Count == 0) return markdown;

        foreach (Match match in matches)
        {
            if (!Guid.TryParse(match.Groups[2].Value, out var mediaId)) continue;
            try
            {
                var content = await mediaService.GetContentAsync(mediaId);
                if (content == null)
                {
                    markdown = markdown.Replace(match.Value, $"*{AppStrings.Format("ImageUnavailableFormat", match.Groups[1].Value)}*");
                    continue;
                }
                var (data, contentType, _) = content.Value;
                var b64 = Convert.ToBase64String(data);
                var dataUri = $"data:{contentType};base64,{b64}";
                markdown = markdown.Replace(match.Value, $"![{match.Groups[1].Value}]({dataUri})");
            }
            catch (Exception)
            {
                markdown = markdown.Replace(match.Value, $"*{AppStrings.Format("ImageUnavailableFormat", match.Groups[1].Value)}*");
            }
        }
        return markdown;
    }

    private void RenderContent(string markdown)
    {
        ContentMarkdown.MarkdownText = ContentFormat.SanitizeMarkdown(markdown);
    }

    // Through CommentService, not the repository: comments are encrypted with the article's key,
    // so the raw row's Text is empty for every comment written by another node (and for our own
    // since they are encrypted too). Text is overwritten with the plaintext for display only.
    private async Task LoadCommentsAsync(Guid articleId, IServiceProvider sp)
    {
        var commentSvc = sp.GetRequiredService<CommentService>();
        var comments = await sp.GetRequiredService<ICommentRepository>().GetByArticleIdAsync(articleId);
        foreach (var c in comments)
        {
            // A protected article's comments are sealed under its passphrase too; without the
            // unlock they read as CommentService.LockedText.
            try { c.Text = await commentSvc.DecryptTextAsync(c, _unlockedPassphrase); }
            catch (Exception) { c.Text = AppStrings.Get("CommentDecryptError"); }
        }
        var ordered = comments.OrderBy(c => c.CreatedAt).ToList();
        BindableLayout.SetItemsSource(CommentsList, ordered);
        NoCommentsLabel.IsVisible = ordered.Count == 0;
        CommentsHeaderLabel.Text = AppStrings.Format("CommentsFormat", ordered.Count);
    }

    private async void OnAddCommentClicked(object? sender, EventArgs e)
    {
        var text = CommentEntry.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // The one busy helper (BMB-311): the button is off and spins; a failure is said in full in a dialog.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            using var scope = _services.CreateScope();
            // CommentService encrypts with the article's key and logs the sync event. The repository's
            // plain CreateAsync stored the text unencrypted and shipped it to every peer in clear.
            await scope.ServiceProvider.GetRequiredService<CommentService>().CreateAsync(_parsedId, text, _unlockedPassphrase);

            CommentEntry.Text = "";
            await LoadCommentsAsync(_parsedId, scope.ServiceProvider);
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async void OnDeleteCommentClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not Comment comment) return;

        bool confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteCommentTitle"), AppStrings.Get("DeleteCommentDesc"),
            AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        await BusyAction.RunAsync(sender as Button, async () =>
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CommentService>().DeleteAsync(comment.Id);

            await LoadCommentsAsync(_parsedId, scope.ServiceProvider);
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async void OnUnlockClicked(object? sender, EventArgs e)
    {
        var pass = UnlockPassEntry.Text ?? "";
        if (pass.Length == 0) { UnlockPassEntry.Focus(); return; }
        if (_protectedBlob == null) return;

        UnlockErrorLabel.IsVisible = false;
        var blob = _protectedBlob;
        // The one busy helper (BMB-311): the button is off and spins through the key derivation; a failure other than a wrong password is
        // said in full in the error line.
        await BusyAction.RunAsync(sender as Button, () => UnlockAsync(blob, pass), new BusyOptions { ResultLabel = UnlockErrorLabel });
    }

    private async Task UnlockAsync(string blob, string pass)
    {
        string plaintext;
        try
        {
            // Argon2id KDF is intentionally CPU-heavy — run it off the UI thread to avoid an ANR.
            // Local, offline decryption of the inner passphrase layer (GCM tag fails on wrong pass).
            plaintext = await Task.Run(() => BeeMemoryBank.Crypto.ProtectedContentCodec.Unwrap(blob, pass));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            UnlockErrorLabel.Text = AppStrings.Get("WrongArticlePassword");
            UnlockErrorLabel.IsVisible = true;
            return;
        }

        UnlockPassEntry.Text = "";
        _rawContent = plaintext; // Raw view now shows the decrypted text, not the blob

        // Hand the verified passphrase to the edit page (short-lived, in-memory) so tapping Edit right
        // after unlocking doesn't re-prompt.
        _services.GetService<Services.MobileUnlockHolder>()?.Remember(_parsedId, pass);
        _unlockedPassphrase = pass;

        // Reset the Raw/Markdown toggle so unlocking after tapping "Raw" (while locked) doesn't land
        // on a blank screen with the stale empty raw label.
        _showingRaw = false;
        ContentRawLabel.IsVisible = false;
        ContentMarkdown.IsVisible = true;
        ToggleViewItem.Text = AppStrings.Get("ShowRaw");

        var rendered = plaintext;
        try
        {
            using var scope = _services.CreateScope();
            var mediaService = scope.ServiceProvider.GetService<MediaService>();
            if (mediaService != null)
                rendered = await ResolveMediaImagesAsync(plaintext, mediaService);
        }
        catch { /* fall back to raw markdown if media resolution fails */ }

        RenderContent(rendered);
        LockCard.IsVisible = false;
        ContentCard.IsVisible = true;

        // The comments were loaded locked; open them with the passphrase just verified. Comments
        // written before they were sealed under the passphrase get sealed now (no-op when none).
        try
        {
            using var commentScope = _services.CreateScope();
            await commentScope.ServiceProvider.GetRequiredService<CommentService>().ReprotectAsync(_parsedId, null, pass);
            await LoadCommentsAsync(_parsedId, commentScope.ServiceProvider);
        }
        catch { /* the article itself is open; comments stay as loaded */ }
    }

    // busy-exempt: opens the edit page, nothing is sent and nothing waits
    private async void OnEditClicked(object? sender, EventArgs e)
    {
        // Protected articles are editable on mobile: the edit page unlocks (or reuses the handoff
        // passphrase) and re-wraps on save.
        await Shell.Current.GoToAsync($"articleEdit?id={_parsedId}");
    }

    // The ⋮ menu's password entry: remove the protection of a protected article, or open the
    // editor to set one on a plain article (the mock-up's "set a password").
    private async void OnPasswordClicked(object? sender, EventArgs e)
    {
        if (!_isProtected)
        {
            await Shell.Current.GoToAsync($"articleEdit?id={_parsedId}");
            return;
        }

        var pass = await this.ShowInputPopupAsync(AppStrings.Get("RemovePasswordTitle"), AppStrings.Get("RemovePasswordDesc"),
            accept: AppStrings.Get("Remove"), cancel: AppStrings.Get("Cancel"),
            placeholder: AppStrings.Get("ArticlePasswordPlaceholder"), isPassword: true);
        if (string.IsNullOrEmpty(pass)) return;

        // The one busy helper (BMB-311): the menu entry is off and says it is working; a failure is said in full in a dialog.
        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
                await articleSvc.UnprotectAsync(_parsedId, pass);
                // Comments were sealed under the passphrase as well; take that layer off them too.
                await scope.ServiceProvider.GetRequiredService<CommentService>().ReprotectAsync(_parsedId, pass, null);
                _services.GetService<Services.MobileUnlockHolder>()?.Clear();
                _cts = new CancellationTokenSource();
                await LoadAsync(_parsedId, _cts.Token); // reload as a now-plaintext article
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("WrongPasswordTitle"), AppStrings.Get("WrongPasswordDesc"));
            }
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async void OnMoveClicked(object? sender, EventArgs e)
    {
        var destination = await MoveSheet.ShowAsync(this, _services);
        if (destination is null) return;

        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ArticleService>().MoveAsync(_parsedId, destination);
            _cts = new CancellationTokenSource();
            await LoadAsync(_parsedId, _cts.Token, isInitial: false);
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        // What the core does: a soft delete that syncs to every device; the trash can restore it.
        bool confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteArticleTitle"), AppStrings.Get("DeleteArticleDesc"),
            AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
            await articleSvc.DeleteAsync(_parsedId);
            await Shell.Current.GoToAsync("..");
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Reload when returning to the page (e.g. after editing) so the content/tags are fresh and a
        // protected article re-locks. Skip the very first appearance — the ArticleId setter already
        // kicked off the initial load.
        if (_appearedBefore)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = LoadAsync(_parsedId, _cts.Token, isInitial: false);
        }
        _appearedBefore = true;
        _services.GetRequiredService<Services.SyncNotificationService>().ClearPendingUpdates();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _cts?.Cancel();
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Pulling the list down asks for a sync round: a request to the computer, not a read (BMB-311). The one busy helper refuses a second round while one runs;
        // the spinner of the pull is the pull's own, so no pop-up; a failure to even ask is said in full.
        try
        {
            await BusyAction.RunAsync(PullRefresh, async () =>
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
                if (!syncNotify.HasPendingUpdates)
                {
                    var syncStatus = _services.GetRequiredService<Services.SyncStatusService>();
                    await syncStatus.SyncNowAsync();
                }

                await LoadAsync(_parsedId, _cts.Token, isInitial: false);
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
        }
        finally
        {
            PullRefresh.IsRefreshing = false;
        }
    }
}
