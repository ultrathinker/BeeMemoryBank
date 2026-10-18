using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>The trash: notes deleted here or on another device, and a way back for each (RestoreService's copy).</summary>
public partial class TrashPage : ContentPage
{
    private readonly NotesService _notes;

    public TrashPage(NotesService notes)
    {
        InitializeComponent();
        _notes = notes;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync() => List.ItemsSource = await Task.Run(() => _notes.TrashAsync());

    private async void OnRestore(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not TrashItem item) return;
        // The one busy helper (BMB-311): the button is off and spins until the note is back; a failure is said in full.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await _notes.RestoreAsync(item.Id);
            await DisplayAlertAsync("Restored", $"\"[RESTORED] {item.Title}\" is at the top of your notes.", "OK");
            await LoadAsync();
        }, new BusyOptions { FailureTitle = "Restore" });
    }
}
