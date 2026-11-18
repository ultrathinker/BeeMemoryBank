using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>The trash: notes deleted here or on another device, and a way back for each (RestoreService's copy).</summary>
public partial class TrashPage : ContentPage
{
    private readonly NotesService _notes;
    private readonly FullSync _sync;
    private readonly FullVault _vault;
    private readonly ILogger<TrashPage> _logger;

    public TrashPage(NotesService notes, FullSync sync, FullVault vault, ILogger<TrashPage> logger)
    {
        InitializeComponent();
        Title = AppStrings.Get("SettingsTrash");
        _notes = notes;
        _sync = sync;
        _vault = vault;
        _logger = logger;
    }

    /// <summary>
    /// An idle lock, the background lock or Lock now can close an encrypted database under the read: this async void catches
    /// and stops quietly when the vault is no longer unlocked - the unlock page takes over. A failure on an open vault is logged and said in the
    /// app's own sentence.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (_vault.IsUnlocked) await LoadAsync();
        }
        catch (Exception ex)
        {
            var unlocked = _vault.IsUnlocked;
            _logger.LogWarning(ex, "Reading the trash failed (vault unlocked: {Unlocked})", unlocked);
            if (unlocked) await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    private async Task LoadAsync()
    {
        var items = await Task.Run(() => _notes.TrashAsync());
        // The vault may have locked during the read: the list of a closed vault is not shown.
        if (_vault.IsUnlocked) List.ItemsSource = items;
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        try
        {
            await BusyAction.RunAsync(Refresher, async () =>
            {
                await _sync.RoundAsync();
                await LoadAsync();
            }, new BusyOptions { FailureTitle = AppStrings.Get("TabSync") });
        }
        finally
        {
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnRestore(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not TrashItem item) return;
        // The one busy helper (BMB-311): the button is off and spins until the note is back; a failure is said in full.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await _notes.RestoreAsync(item.Id);
            await AppDialog.AlertAsync(this, AppStrings.Get("RestoredTitle"), AppStrings.Format("RestoredFormat", item.Title));
            await LoadAsync();
        }, new BusyOptions { FailureTitle = "Restore" });
    }
}
