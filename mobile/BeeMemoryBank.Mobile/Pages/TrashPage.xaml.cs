using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public sealed record TrashRow(Guid Id, string Title, string DeletedAtText);

/// <summary>
/// The trash: articles deleted here or on another device, and a way back for each. The listing and the
/// restore are the core's own (<see cref="HardDeleteService"/> / <see cref="RestoreService"/>), the same
/// ones the iPhone TrashPage uses — a restore brings the article back as "[RESTORED] title" at the top.
/// </summary>
public partial class TrashPage : ContentPage
{
    private readonly IServiceProvider _services;

    public TrashPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    /// <summary>
    /// The idle timer, leaving the app or Lock now can close an encrypted database under the read: this async void catches
    /// and stops quietly when the vault is no longer unlocked - the unlock page takes over. A failure on an open vault is logged and said in the
    /// app's own sentence.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (IsUnlocked) await LoadAsync();
        }
        catch (Exception ex)
        {
            var unlocked = IsUnlocked;
            System.Diagnostics.Debug.WriteLine($"Reading the trash failed (vault unlocked: {unlocked}): {ex}");
            if (unlocked) await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    /// <summary>The vault is unlocked now: the page reads only then, and looks again after the read.</summary>
    private bool IsUnlocked => _services.GetService<SessionService>()?.IsUnlocked == true;

    private async Task LoadAsync()
    {
        var items = await Task.Run(async () =>
        {
            using var scope = _services.CreateScope();
            var page = await scope.ServiceProvider.GetRequiredService<HardDeleteService>()
                .ListAsync(1, 200, null, HardDeleteStatusFilter.DeletedOnly, CancellationToken.None);
            return page.Items
                .Where(i => i.Type == "article" && i.Id is not null)
                .OrderByDescending(i => i.UpdatedAt)
                .Select(i => new TrashRow(
                    i.Id!.Value,
                    i.Title,
                    AppStrings.Format("TrashDeletedAtFormat", Services.ContentFormat.FullDate(i.UpdatedAt))))
                .ToList();
        });
        // The vault may have locked during the read: the list of a closed vault is not shown.
        if (IsUnlocked) TrashList.ItemsSource = items;
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not TrashRow row) return;
        // The one busy helper (BMB-311): the button is off and spins until the article is back.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RestoreService>().RestoreArticleAsync(row.Id);
            });
            await AppDialog.AlertAsync(this, AppStrings.Get("TrashRestoredTitle"),
                AppStrings.Format("TrashRestoredFormat", row.Title));
            await LoadAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }
}
