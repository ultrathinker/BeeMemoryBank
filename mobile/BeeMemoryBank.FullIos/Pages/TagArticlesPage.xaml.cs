using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>The articles carrying one tag (ConceptTagService.SearchByConceptAsync); tap opens the note.</summary>
public partial class TagArticlesPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private string _tag = "";

    public TagArticlesPage(IServiceProvider services, NotesService notes)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
    }

    public TagArticlesPage For(string tag)
    {
        _tag = tag;
        Title = "#" + tag;
        HeadingLabel.Text = Title;
        return this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            ArticlesList.ItemsSource = await Task.Run(() => _notes.ArticlesByTagAsync(_tag));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Tag articles load failed: " + ex);
            await Controls.AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    // busy-exempt: opens the note tapped, nothing is sent and nothing waits
    private async void OnArticleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not TagArticleItem item) return;
        await Navigation.PushAsync(_services.GetRequiredService<NotePage>().For(item.Id));
    }
}
