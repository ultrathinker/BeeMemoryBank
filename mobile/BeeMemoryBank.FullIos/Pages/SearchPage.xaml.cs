using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>Search as you type (after a short pause): titles, folders and ids through the full-text index, and the notes' text when the switch is on.</summary>
public partial class SearchPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private CancellationTokenSource? _typing;

    public SearchPage(IServiceProvider services, NotesService notes)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrEmpty(Query.Text)) Query.Focus();
    }

    /// <summary>Searches for <paramref name="query"/> as if it had been typed.</summary>
    public void Find(string query) => Query.Text = query;

    private async void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        _typing?.Cancel();
        _typing = new CancellationTokenSource();
        var token = _typing.Token;
        try { await Task.Delay(350, token); }
        catch (TaskCanceledException) { return; }
        await RunAsync(token);
    }

    private async void OnSearch(object? sender, EventArgs e) => await RunAsync(CancellationToken.None);

    private async void OnInTextToggled(object? sender, ToggledEventArgs e) => await RunAsync(CancellationToken.None);

    private async Task RunAsync(CancellationToken token)
    {
        var query = Query.Text ?? "";
        var inText = InText.IsToggled;
        try
        {
            var found = await Task.Run(() => _notes.SearchAsync(query, inText), token);
            if (token.IsCancellationRequested) return;
            Results.ItemsSource = found;
            if (found.Count == 0 && query.Trim().Length > 0) EmptyLabel.Text = "Nothing found.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Results.ItemsSource = null;
            EmptyLabel.Text = ex.Message;
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
}
