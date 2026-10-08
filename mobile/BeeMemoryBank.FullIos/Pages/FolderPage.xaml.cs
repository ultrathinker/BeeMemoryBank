using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>A folder: its sub-folders, then its notes. Tap to open, swipe a note left to move it to the trash, + to write a new one here.</summary>
public partial class FolderPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private readonly FullSync _sync;
    private string _path = "/";

    public FolderPage(IServiceProvider services, NotesService notes, FullSync sync)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
        _sync = sync;
    }

    /// <summary>Which folder this page shows (the top one unless set).</summary>
    public FolderPage At(string path)
    {
        _path = NotesService.NormalizePath(path);
        return this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Title = NotesService.FolderTitle(_path);
        PathLabel.Text = _path == "/" ? "" : _path;
        PathLabel.IsVisible = _path != "/";
        _sync.Changed += OnSyncChanged;
        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _sync.Changed -= OnSyncChanged;
    }

    // A round that brought something in refreshes the list (the note written on the computer appears without a pull).
    private void OnSyncChanged()
    {
        if (!_sync.IsRunning && _sync.LastRound is { Applied: > 0 }) MainThread.BeginInvokeOnMainThread(() => _ = LoadAsync());
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await _sync.RoundAsync();
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async Task LoadAsync()
    {
        try
        {
            List.ItemsSource = await Task.Run(() => _notes.ListFolderAsync(_path));
        }
        catch (Exception ex)
        {
            EmptyLabel.Text = "The folder could not be read: " + ex.Message;
            List.ItemsSource = null;
        }
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NoteListItem item) return;
        if (item.IsFolder)
            await Navigation.PushAsync(_services.GetRequiredService<FolderPage>().At(item.Path));
        else if (item.NoteId is { } id)
            await Navigation.PushAsync(_services.GetRequiredService<NotePage>().For(id));
    }

    private async void OnNewNote(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().New(_path));

    private async void OnNewFolder(object? sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("New folder", $"A folder inside {NotesService.FolderTitle(_path)}:", "Create", "Cancel", "Name", 100);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _notes.CreateFolderAsync(_path, name);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("New folder", ex.Message, "OK");
        }
    }

    private async void OnSwipeDelete(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NoteListItem { NoteId: { } id } item) return;
        try
        {
            await _notes.DeleteAsync(id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(item.Name, ex.Message, "OK");
        }
    }

    private async void OnTrash(object? sender, EventArgs e) => await Navigation.PushAsync(_services.GetRequiredService<TrashPage>());
}
