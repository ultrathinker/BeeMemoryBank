using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// All concept tags (the Android TagsPage's behaviour, the iPhone design): search, the tag's
/// articles on tap, and the ⋮ sheet with rename, merge and delete through ConceptTagService.
/// </summary>
public partial class TagsPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;

    private sealed record TagRow(string Name, string CountText);

    public TagsPage(IServiceProvider services, NotesService notes)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
        Title = AppStrings.Get("AllTags");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadTagsAsync();
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            var tags = await Task.Run(() => _notes.AllTagsAsync(SearchEntry.Text));
            TagsList.ItemsSource = tags.Select(t => new TagRow(t.Name, ContentFormat.ItemCount(t.Count))).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Tags load failed: " + ex);
            await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => _ = LoadTagsAsync();

    // busy-exempt: opens the tag's articles, nothing is sent and nothing waits
    private async void OnTagTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not TagRow row) return;
        await Navigation.PushAsync(_services.GetRequiredService<TagArticlesPage>().For(row.Name));
    }

    private async void OnTagOptionsClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not TagRow row) return;
        var action = await ShowTagActionsAsync(row.Name);
        switch (action)
        {
            case "rename": await RenameTagAsync(row.Name); break;
            case "merge": await MergeTagAsync(row.Name); break;
            case "delete": await DeleteTagAsync(row.Name); break;
        }
    }

    /// <summary>The row's sheet: rename, merge, delete and a quiet cancel; closes before it answers.</summary>
    private async Task<string> ShowTagActionsAsync(string tagName)
    {
        var chosen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        var sheet = new BottomSheet.BottomSheetPage(stack, tagName);

        void Add(string key, string styleKey, string action)
        {
            var button = new Button { Text = AppStrings.Get(key), HorizontalOptions = LayoutOptions.Fill };
            if (resources is not null && resources.TryGetValue(styleKey, out var s) && s is Style style) button.Style = style;
            button.Clicked += async (_, _) =>
            {
                answered = true;
                await sheet.CloseAsync();
                chosen.TrySetResult(action);
            };
            stack.Children.Add(button);
        }

        Add("Rename", "SecondaryButton", "rename");
        Add("MergeTag", "SecondaryButton", "merge");
        Add("Delete", "DangerButton", "delete");
        var cancel = new Button { Text = AppStrings.Get("Cancel"), HorizontalOptions = LayoutOptions.Fill };
        if (resources is not null && resources.TryGetValue("TextButton", out var ts) && ts is Style textStyle) cancel.Style = textStyle;
        cancel.Clicked += async (_, _) =>
        {
            answered = true;
            await sheet.CloseAsync();
            chosen.TrySetResult("none");
        };
        stack.Children.Add(cancel);
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult("none"); };
        await Navigation.PushModalAsync(sheet);
        return await chosen.Task;
    }

    private async Task RenameTagAsync(string oldName)
    {
        var newName = await this.ShowInputPopupAsync(AppStrings.Get("RenameTitle"), AppStrings.Get("NewNameLabel"),
            AppStrings.Get("Save"), AppStrings.Get("Cancel"), initialValue: oldName, maxLength: 100);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == oldName) return;
        await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(() => _notes.RenameTagAsync(oldName, newName));
            await LoadTagsAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async Task MergeTagAsync(string source)
    {
        var target = await this.ShowInputPopupAsync(AppStrings.Get("MergeTagTitle"), AppStrings.Get("MergeTagLabel"),
            AppStrings.Get("MergeTag"), AppStrings.Get("Cancel"), placeholder: source, maxLength: 100);
        if (string.IsNullOrWhiteSpace(target) || target.Trim() == source) return;
        await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(() => _notes.MergeTagAsync(source, target));
            await LoadTagsAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async Task DeleteTagAsync(string tagName)
    {
        if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteTagTitle"),
                AppStrings.Format("DeleteTagDescFormat", tagName), AppStrings.Get("Delete"), danger: true)) return;
        await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(() => _notes.DeleteTagAsync(tagName));
            await LoadTagsAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }
}
