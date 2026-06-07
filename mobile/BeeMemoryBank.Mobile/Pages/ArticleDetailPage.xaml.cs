using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Indiko.Maui.Controls.Markdown.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(ArticleId), "id")]
public partial class ArticleDetailPage : ContentPage
{
    private readonly IServiceProvider _services;
    private Guid _parsedId;
    private string _rawContent = "";
    private bool _showingRaw = false;
    // Phase-1 data-loss guard: a protected article's body is a passphrase-encrypted BMBENC1 blob.
    // The mobile app has no unlock UI yet, so we show a read-only placeholder and block editing —
    // otherwise the user would edit/overwrite the ciphertext and destroy the data on next sync.
    private bool _isProtected = false;
    private string? _protectedBlob; // the BMBENC1 body; unwrapped on-device after the user enters the passphrase
    private CancellationTokenSource? _cts;
    private List<RelatedArticle> _relatedAll = new();
    private int _relatedShown;
    private const int RelatedPageSize = 3;

    public string ArticleId
    {
        set
        {
            if (Guid.TryParse(value, out _parsedId))
            {
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
        ContentMarkdown.Theme = MarkdownThemeDefaults.Dark;
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
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
                await DisplayAlert("Error", "Article not found.", "OK");
                await Shell.Current.GoToAsync("..");
                return;
            }

            Title = article.Title;
            TitleLabel.Text = (article.Protected ? "🔒 " : "") + article.Title;
            PathLabel.Text = article.TreePath;
            UpdatedLabel.Text = article.UpdatedAt.ToString("yyyy-MM-dd HH:mm");

            TitleLabel.IsVisible = true;

            var conceptTags = await conceptTagSvc.GetByArticleIdAsync(id);
            if (ct.IsCancellationRequested) return;
            if (conceptTags.Count > 0)
            {
                BindableLayout.SetItemsSource(ConceptTagsLayout, conceptTags);
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
                    LockHintLabel.Text = "💡 Hint: " + article.ProtectionHint;
                    LockHintLabel.IsVisible = true;
                }
                // Show the unlock card; the content stays hidden until the passphrase is entered.
                LockCard.IsVisible = true;
            }
            else
            {
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
            RenderContent($"**Error loading content:** {ex.Message}");
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
        RelatedHeaderLabel.Text = $"Related Articles ({total})";
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

    private async void OnRelatedArticleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is Guid relatedId)
        {
            await Shell.Current.GoToAsync($"articleDetail?id={relatedId}");
        }
    }

    private void OnToggleViewClicked(object? sender, EventArgs e)
    {
        _showingRaw = !_showingRaw;
        if (_showingRaw)
        {
            ContentMarkdown.IsVisible = false;
            ContentRawLabel.Text = _rawContent;
            ContentRawLabel.IsVisible = true;
            ToggleViewItem.Text = "MD";
        }
        else
        {
            ContentRawLabel.IsVisible = false;
            ContentMarkdown.IsVisible = true;
            ToggleViewItem.Text = "Raw";
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
                    markdown = markdown.Replace(match.Value, $"*[Image unavailable: {match.Groups[1].Value}]*");
                    continue;
                }
                var (data, contentType, _) = content.Value;
                var b64 = Convert.ToBase64String(data);
                var dataUri = $"data:{contentType};base64,{b64}";
                markdown = markdown.Replace(match.Value, $"![{match.Groups[1].Value}]({dataUri})");
            }
            catch (Exception)
            {
                markdown = markdown.Replace(match.Value, $"*[Image: {match.Groups[1].Value}]*");
            }
        }
        return markdown;
    }

    // Strip non-data: image links and dangerous raw HTML before native rendering. Articles can be
    // authored on any peer; without this an external image (e.g. ![](http://attacker/pixel.png))
    // would be auto-fetched by the native Image control, leaking the reader's IP/read-activity and
    // defeating the E2E privacy of the vault. Media is already inlined as data: URIs upstream.
    private static readonly Regex _externalImage =
        new(@"!\[([^\]]*)\]\(\s*(?!data:)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex _dangerousHtml =
        new(@"<\s*/?\s*(script|iframe|object|embed|form|meta|link|style|base)\b[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string SanitizeMarkdown(string markdown)
    {
        markdown = _externalImage.Replace(markdown, "*[external image removed]*");
        markdown = _dangerousHtml.Replace(markdown, string.Empty);
        return markdown;
    }

    private void RenderContent(string markdown)
    {
        ContentMarkdown.MarkdownText = SanitizeMarkdown(markdown);
    }

    private async Task LoadCommentsAsync(Guid articleId, IServiceProvider sp)
    {
        var commentRepo = sp.GetRequiredService<ICommentRepository>();
        var comments = await commentRepo.GetByArticleIdAsync(articleId);
        CommentsList.ItemsSource = comments.OrderBy(c => c.CreatedAt).ToList();
    }

    private async void OnAddCommentClicked(object? sender, EventArgs e)
    {
        var text = CommentEntry.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            using var scope = _services.CreateScope();
            var commentRepo = scope.ServiceProvider.GetRequiredService<ICommentRepository>();
            var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();

            var comment = await commentRepo.CreateAsync(_parsedId, text);
            await eventLogger.LogCommentCreateAsync(comment);

            CommentEntry.Text = "";
            await LoadCommentsAsync(_parsedId, scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnDeleteCommentClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.BindingContext is not Comment comment) return;

        bool confirmed = await DisplayAlert("Delete Comment", "Delete this comment?", "Delete", "Cancel");
        if (!confirmed) return;

        try
        {
            using var scope = _services.CreateScope();
            var commentRepo = scope.ServiceProvider.GetRequiredService<ICommentRepository>();
            var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();

            await commentRepo.DeleteAsync(comment.Id);
            await eventLogger.LogCommentDeleteAsync(comment.CommentId);

            await LoadCommentsAsync(_parsedId, scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnUnlockClicked(object? sender, EventArgs e)
    {
        var pass = UnlockPassEntry.Text ?? "";
        if (pass.Length == 0) { UnlockPassEntry.Focus(); return; }
        if (_protectedBlob == null) return;

        UnlockErrorLabel.IsVisible = false;
        var blob = _protectedBlob;
        string plaintext;
        try
        {
            // Argon2id KDF is intentionally CPU-heavy — run it off the UI thread to avoid an ANR.
            // Local, offline decryption of the inner passphrase layer (GCM tag fails on wrong pass).
            plaintext = await Task.Run(() => BeeMemoryBank.Crypto.ProtectedContentCodec.Unwrap(blob, pass));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            UnlockErrorLabel.Text = "Wrong password.";
            UnlockErrorLabel.IsVisible = true;
            return;
        }

        UnlockPassEntry.Text = "";
        _rawContent = plaintext; // Raw view now shows the decrypted text, not the blob

        // Reset the Raw/Markdown toggle so unlocking after tapping "Raw" (while locked) doesn't land
        // on a blank screen with the stale empty raw label.
        _showingRaw = false;
        ContentRawLabel.IsVisible = false;
        ContentMarkdown.IsVisible = true;
        ToggleViewItem.Text = "Raw";

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
    }

    private async void OnEditClicked(object? sender, EventArgs e)
    {
        if (_isProtected)
        {
            await DisplayAlert("Protected article",
                "This article is password-protected. Editing it on mobile is not supported yet — open it in the web app to unlock and edit.",
                "OK");
            return;
        }
        await Shell.Current.GoToAsync($"articleEdit?id={_parsedId}");
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        bool confirmed = await DisplayAlert("Delete", "Delete this article?", "Delete", "Cancel");
        if (!confirmed) return;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
            await articleSvc.DeleteAsync(_parsedId);
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
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
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
        if (!syncNotify.HasPendingUpdates)
        {
            var syncStatus = _services.GetRequiredService<Services.SyncStatusService>();
            await syncStatus.SyncNowAsync();
        }
        
        await LoadAsync(_parsedId, _cts.Token);
        syncNotify.ClearPendingUpdates();
        PullRefresh.IsRefreshing = false;
    }
}
