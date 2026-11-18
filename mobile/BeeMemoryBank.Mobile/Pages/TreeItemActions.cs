using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// The row actions of the tree (the mock-up's item-actions sheet): rename, move, delete — for
/// folders and articles alike, through the same core services the iPhone app uses. What the
/// delete dialog says is what the core really does: an article is soft-deleted and the delete
/// syncs to every device (restorable from the trash); a folder takes its subfolders with it and
/// the articles inside move to the root.
/// </summary>
internal static class TreeItemActions
{
    public static async Task RunAsync(ContentPage page, IServiceProvider services, TreeListItem item, Func<Task> reload)
    {
        var action = await ItemActionsSheet.ShowAsync(page, item.Name);
        switch (action)
        {
            case ItemActionsSheet.RowAction.Rename:
                await RenameAsync(page, services, item, reload);
                break;
            case ItemActionsSheet.RowAction.Move:
                await MoveAsync(page, services, item, reload);
                break;
            case ItemActionsSheet.RowAction.Delete:
                await DeleteAsync(page, services, item, reload);
                break;
        }
    }

    private static async Task RenameAsync(ContentPage page, IServiceProvider services, TreeListItem item, Func<Task> reload)
    {
        var newName = await page.ShowInputPopupAsync(AppStrings.Get("RenameTitle"), AppStrings.Get("NewNameLabel"),
            accept: AppStrings.Get("Save"), cancel: AppStrings.Get("Cancel"), initialValue: item.Name, maxLength: 200);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;

        // The one busy helper (BMB-311): a second tap cannot start a second rename; a failure is said in full in a dialog.
        await BusyAction.RunAsync(null, async () =>
        {
            using var scope = services.CreateScope();
            if (item.IsFolder && item.FolderPath is not null)
            {
                var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
                var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
                var folder = await folderRepo.GetByPathAsync(item.FolderPath);
                if (folder == null) return;
                await folderSvc.RenameAsync(folder.Id, newName.Trim());
            }
            else if (item.ArticleId is { } id)
            {
                var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
                await articleSvc.UpdateAsync(id, title: newName.Trim());
            }
            await reload();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private static async Task MoveAsync(ContentPage page, IServiceProvider services, TreeListItem item, Func<Task> reload)
    {
        var destination = await MoveSheet.ShowAsync(page, services, item.IsFolder ? item.FolderPath : null);
        if (destination is null) return;

        await BusyAction.RunAsync(null, async () =>
        {
            using var scope = services.CreateScope();
            if (item.IsFolder && item.FolderPath is not null)
            {
                var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
                var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
                var folder = await folderRepo.GetByPathAsync(item.FolderPath);
                if (folder == null) return;
                await folderSvc.MoveAsync(folder.Id, destination);
            }
            else if (item.ArticleId is { } id)
            {
                var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
                await articleSvc.MoveAsync(id, destination);
            }
            await reload();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private static async Task DeleteAsync(ContentPage page, IServiceProvider services, TreeListItem item, Func<Task> reload)
    {
        var title = item.IsFolder ? AppStrings.Get("DeleteFolderTitle") : AppStrings.Get("DeleteArticleTitle");
        var desc = item.IsFolder ? AppStrings.Get("DeleteFolderDesc") : AppStrings.Get("DeleteArticleDesc");
        var confirmed = await AppDialog.ConfirmAsync(page, title, desc,
            AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        await BusyAction.RunAsync(null, async () =>
        {
            using var scope = services.CreateScope();
            if (item.IsFolder && item.FolderPath is not null)
            {
                var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
                var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
                var folder = await folderRepo.GetByPathAsync(item.FolderPath);
                if (folder == null) return;
                await folderSvc.DeleteAsync(folder.Id);
            }
            else if (item.ArticleId is { } id)
            {
                var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
                await articleSvc.DeleteAsync(id);
            }
            await reload();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }
}
