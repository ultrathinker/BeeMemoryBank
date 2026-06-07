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

    public string ArticleId
    {
        set
        {
            if (Guid.TryParse(value, out var id))
            {
                _articleId = id;
                Title = "Edit Article";
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
        Title = "New Article";
        PathLabel.Text = "/";
        // Protect-on-create is offered for NEW articles only; LoadAsync() hides it for existing ones.
        ProtectSection.IsVisible = true;
        FolderPickerPage.FolderSelected = path => PathLabel.Text = path;
    }

    private void OnProtectToggled(object? sender, ToggledEventArgs e)
    {
        ProtectFields.IsVisible = e.Value;
    }

    // Edit gate for an existing protected article opened without a fresh read→edit handoff.
    private async void OnEditUnlockClicked(object? sender, EventArgs e)
    {
        var pass = EditUnlockPassEntry.Text ?? "";
        if (pass.Length == 0) { EditUnlockPassEntry.Focus(); return; }
        if (_protectedBlobForEdit == null) return;

        EditUnlockErrorLabel.IsVisible = false;
        var blob = _protectedBlobForEdit;
        string pt;
        try
        {
            pt = await Task.Run(() => BeeMemoryBank.Crypto.ProtectedContentCodec.Unwrap(blob, pass));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            EditUnlockErrorLabel.Text = "Wrong password.";
            EditUnlockErrorLabel.IsVisible = true;
            return;
        }

        _editPassphrase = pass;
        EditUnlockPassEntry.Text = "";
        ContentEditor.Text = pt;
        EditUnlockGate.IsVisible = false;
        ContentEditor.IsVisible = true;
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
        // Reset protected state up-front so a reused page can never carry stale flags into a save.
        _isProtectedArticle = false;
        _editPassphrase = null;
        _protectedBlobForEdit = null;

        try
        {
            using var scope = _services.CreateScope();
            var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var conceptTagSvc = scope.ServiceProvider.GetRequiredService<ConceptTagService>();

            var article = await articleSvc.GetMetadataAsync(id);
            if (article == null) return;

            TitleEntry.Text = article.Title;
            PathLabel.Text = article.TreePath;
            ProtectSection.IsVisible = false; // existing article: protect-on-create doesn't apply

            var conceptTags = await conceptTagSvc.GetByArticleIdAsync(id);
            ConceptTagsEntry.Text = string.Join(", ", conceptTags);

            var content = await articleSvc.GetContentAsync(id);

            if (article.Protected)
            {
                _isProtectedArticle = true;
                _protectedBlobForEdit = content; // BMBENC1 body; decrypted after unlock/handoff

                // Try the read→edit handoff first (passphrase verified moments ago on the detail page).
                var handoff = _services.GetService<MobileUnlockHolder>()?.Take(id);
                if (handoff != null)
                {
                    try
                    {
                        var pt = await Task.Run(() => BeeMemoryBank.Crypto.ProtectedContentCodec.Unwrap(_protectedBlobForEdit, handoff));
                        _editPassphrase = handoff;
                        ContentEditor.Text = pt;
                        return; // unlocked via handoff — no gate needed
                    }
                    catch { /* stale handoff — fall through to the gate */ }
                }

                // No (valid) handoff → show the unlock gate; the editor stays hidden until unlocked.
                EditUnlockGate.IsVisible = true;
                ContentEditor.IsVisible = false;
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
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!_loaded)
        {
            ShowError("Still loading — please wait a moment and try again.");
            return;
        }
        var title = TitleEntry.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            ShowError("Title is required.");
            return;
        }

        var path = PathLabel.Text?.Trim() ?? "/";
        if (!path.StartsWith('/')) path = "/" + path;

        var conceptTags = (ConceptTagsEntry.Text ?? "")
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
                if (_isProtectedArticle)
                {
                    if (_editPassphrase == null) { ShowError("Unlock the article first."); return; }
                    try
                    {
                        // Re-wrap the new body under the same passphrase (verifies it first).
                        await articleSvc.UpdateProtectedContentAsync(_articleId.Value, content, _editPassphrase);
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        ShowError("Wrong password.");
                        return;
                    }
                    // Metadata + tags only (no body — keeps it protected).
                    await articleSvc.UpdateAsync(_articleId.Value, title, path, conceptTags, null);
                }
                else
                {
                    await articleSvc.UpdateAsync(_articleId.Value, title, path, conceptTags, content);
                }
                await Shell.Current.GoToAsync($"..?created={_articleId.Value}");
            }
            else
            {
                string? hint = null;
                if (ProtectSwitch.IsToggled)
                {
                    var pass = ProtectPassEntry.Text ?? "";
                    if (pass.Length < 4) { ShowError("Password must be at least 4 characters."); return; }
                    if (pass != (ProtectPass2Entry.Text ?? "")) { ShowError("Passwords do not match."); return; }
                    // Wrap locally so the very first CREATE event carries only ciphertext.
                    content = BeeMemoryBank.Crypto.ProtectedContentCodec.Wrap(content, pass);
                    hint = string.IsNullOrWhiteSpace(ProtectHintEntry.Text) ? null : ProtectHintEntry.Text.Trim();
                }
                var article = await articleSvc.CreateAsync(title, path, conceptTags, content, hint);
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
