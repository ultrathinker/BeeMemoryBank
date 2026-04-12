using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(ArticleId), "id")]
[QueryProperty(nameof(InitialPath), "path")]
public partial class ArticleEditPage : ContentPage
{
    private readonly IServiceProvider _services;
    private Guid? _articleId;

    public string ArticleId
    {
        set
        {
            if (Guid.TryParse(value, out var id))
            {
                _articleId = id;
                Title = "Edit Article";
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
        Title = "New Article";
        PathLabel.Text = "/";
        FolderPickerPage.FolderSelected = path => PathLabel.Text = path;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_articleId == null && ShareIntentHandler.PendingText != null)
        {
            ContentEditor.Text = ShareIntentHandler.PendingText;
            ShareIntentHandler.PendingText = null;
        }
    }

    private async Task LoadAsync(Guid id)
    {
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();

            var article = await articleSvc.GetMetadataAsync(id);
            if (article == null) return;

            TitleEntry.Text = article.Title;
            PathLabel.Text = article.TreePath;
            TagsEntry.Text = string.Join(", ", article.Tags);

            var content = await articleSvc.GetContentAsync(id);
            ContentEditor.Text = content;
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

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var title = TitleEntry.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            ShowError("Title is required.");
            return;
        }

        var path = PathLabel.Text?.Trim() ?? "/";
        if (!path.StartsWith('/')) path = "/" + path;

        var tags = (TagsEntry.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var content = ContentEditor.Text ?? "";

        ErrorLabel.IsVisible = false;
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();

            if (_articleId.HasValue)
            {
                await articleSvc.UpdateAsync(_articleId.Value, title, path, tags, content);
                await Shell.Current.GoToAsync($"..?created={_articleId.Value}");
            }
            else
            {
                var article = await articleSvc.CreateAsync(title, path, tags, content);
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

    private async void OnAddImageClicked(object? sender, EventArgs e)
    {
        var action = await DisplayActionSheet("Add Image", "Cancel", null, "Photo Library", "Take Photo");
        if (action == "Cancel" || action == null) return;

        try
        {
            FileResult? photo = action == "Take Photo"
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await MediaPicker.Default.PickPhotoAsync();

            if (photo == null) return;

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
        }
        catch (Exception ex)
        {
            ShowError(GetErrorMessage(ex));
        }
    }

    private async void OnChooseFolderClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("folderPicker");
    }
}
