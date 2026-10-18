using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// Writing a note: title, folder, Markdown text. Save is at the bottom, under the thumb, and in the bar. Leaving with unsaved changes asks
/// first. Typing counts as activity for the idle lock (the keyboard's touches are not the app's).
/// </summary>
public partial class EditNotePage : ContentPage
{
    private readonly NotesService _notes;
    private readonly AppFlow _flow;
    private Guid? _id;
    private string? _passphrase;
    private bool _dirty;
    private bool _loading;

    public EditNotePage(NotesService notes, AppFlow flow)
    {
        InitializeComponent();
        _notes = notes;
        _flow = flow;
        Shell.SetBackButtonBehavior(this, new BackButtonBehavior { Command = new Command(async () => await LeaveAsync()) });
    }

    public EditNotePage New(string folder)
    {
        Title = "New note";
        Fill("", folder, "");
        return this;
    }

    public EditNotePage Existing(NoteView note, string text, string? passphrase)
    {
        _id = note.Id;
        _passphrase = passphrase;
        Title = "Edit";
        Fill(note.Title, note.Path, text);
        return this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_id is null && string.IsNullOrEmpty(TitleEntry.Text)) TitleEntry.Focus();
    }

    private void Fill(string title, string path, string body)
    {
        _loading = true;
        TitleEntry.Text = title;
        PathEntry.Text = path;
        BodyEditor.Text = body;
        _loading = false;
        _dirty = false;
    }

    private void OnChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        _flow.Activity();
    }

    private async void OnSave(object? sender, EventArgs e) => await SaveAsync();

    // The one busy helper (BMB-311): the Save button (also for the toolbar entry and the "Save" of the unsaved-changes question) is off and
    // spins until the note is saved; the whole error is written under it.
    private async Task<bool> SaveAsync()
    {
        ErrorLabel.IsVisible = false;
        return await BusyAction.RunAsync(SaveButton, async () =>
        {
            var title = TitleEntry.Text ?? "";
            var path = PathEntry.Text ?? "/";
            var body = BodyEditor.Text ?? "";
            _id = await Task.Run(() => _notes.SaveAsync(_id, title, path, body, _passphrase));
            _dirty = false;
            await Navigation.PopAsync();
        }, new BusyOptions { ResultLabel = ErrorLabel });
    }

    private async Task LeaveAsync()
    {
        if (_dirty)
        {
            var choice = await DisplayActionSheetAsync("Unsaved changes", "Keep editing", "Discard", "Save");
            if (choice == "Save")
            {
                await SaveAsync();
                return;
            }
            if (choice != "Discard") return;
        }
        _dirty = false;
        await Navigation.PopAsync();
    }
}
