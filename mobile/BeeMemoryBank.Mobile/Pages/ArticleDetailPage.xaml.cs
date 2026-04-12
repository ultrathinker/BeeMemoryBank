using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Markdig;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(ArticleId), "id")]
public partial class ArticleDetailPage : ContentPage
{
    private readonly IServiceProvider _services;
    private Guid _parsedId;
    private string _rawContent = "";
    private bool _showingRaw = false;

    public string ArticleId
    {
        set
        {
            if (Guid.TryParse(value, out _parsedId))
                _ = LoadAsync(_parsedId);
        }
    }

    public ArticleDetailPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    private async Task LoadAsync(Guid id)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        TitleLabel.IsVisible = false;
        MetaCard.IsVisible = false;
        ContentCard.IsVisible = false;
        CommentsCard.IsVisible = false;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();

            var article = await articleSvc.GetMetadataAsync(id);
            if (article == null)
            {
                await DisplayAlert("Error", "Article not found.", "OK");
                await Shell.Current.GoToAsync("..");
                return;
            }

            Title = article.Title;
            TitleLabel.Text = article.Title;
            PathLabel.Text = article.TreePath;
            TagsLabel.Text = article.Tags.Count > 0 ? string.Join(", ", article.Tags) : "—";
            UpdatedLabel.Text = article.UpdatedAt.ToString("yyyy-MM-dd HH:mm");

            TitleLabel.IsVisible = true;
            MetaCard.IsVisible = true;

            var content = await articleSvc.GetContentAsync(id);
            _rawContent = content;

            var mediaService = scope.ServiceProvider.GetService<MediaService>();
            if (mediaService != null)
                content = await ResolveMediaImagesAsync(content, mediaService);

            RenderContent(content);
            ContentCard.IsVisible = true;

            await LoadCommentsAsync(id, scope.ServiceProvider);
            CommentsCard.IsVisible = true;
        }
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

    private void OnToggleViewClicked(object? sender, EventArgs e)
    {
        _showingRaw = !_showingRaw;
        if (_showingRaw)
        {
            ContentWebView.IsVisible = false;
            ContentRawLabel.Text = _rawContent;
            ContentRawLabel.IsVisible = true;
            ToggleViewItem.Text = "MD";
        }
        else
        {
            ContentRawLabel.IsVisible = false;
            ContentWebView.IsVisible = true;
            ToggleViewItem.Text = "Raw";
        }
    }

    private static readonly MarkdownPipeline _mdPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

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
                var (data, contentType, _) = await mediaService.GetContentAsync(mediaId);
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

    private void RenderContent(string markdown)
    {
        var html = Markdig.Markdown.ToHtml(markdown, _mdPipeline);
        var styledHtml = $$"""
            <html><head>
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src * data: blob:; font-src 'none'; script-src 'none';">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
              body { background:#1c1017; color:#e8d8df; font-family:sans-serif; font-size:16px; padding:8px; margin:0; line-height:1.6; }
              a { color:#c0748a; }
              code { background:#2a1520; padding:2px 5px; border-radius:3px; font-size:14px; }
              pre { background:#2a1520; padding:10px; border-radius:4px; overflow-x:auto; }
              pre code { padding:0; background:transparent; }
              blockquote { border-left:3px solid #c0748a; margin:0 0 0 4px; padding-left:12px; color:#a09098; }
              h1,h2,h3,h4,h5,h6 { color:#f0a8c0; margin-top:16px; }
              hr { border:none; border-top:1px solid #3a2530; }
              table { border-collapse:collapse; width:100%; }
              th,td { border:1px solid #3a2530; padding:6px 10px; }
              th { background:#2a1520; }
              img { max-width:100%; }
              ul,ol { padding-left:20px; }
            </style>
            </head><body>{{html}}</body></html>
            """;
        ContentWebView.Source = new HtmlWebViewSource { Html = styledHtml };
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

    private async void OnEditClicked(object? sender, EventArgs e)
    {
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
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
        if (!syncNotify.HasPendingUpdates)
        {
            var syncStatus = _services.GetRequiredService<Services.SyncStatusService>();
            await syncStatus.SyncNowAsync();
        }
        
        await LoadAsync(_parsedId);
        syncNotify.ClearPendingUpdates();
        PullRefresh.IsRefreshing = false;
    }
}
