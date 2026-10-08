namespace BeeMemoryBank.FullIos.Pages;

/// <summary>What shows while the database is prepared at start, and why the app cannot go on if it could not be.</summary>
public partial class StartPage : ContentPage
{
    public StartPage() => InitializeComponent();

    public void ShowProblem(string text)
    {
        Busy.IsRunning = false;
        Problem.Text = text;
        Problem.IsVisible = true;
    }
}
