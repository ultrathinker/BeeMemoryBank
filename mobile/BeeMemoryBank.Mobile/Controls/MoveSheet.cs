using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The mock-up's move dialog (move-article-dialog) as a bottom sheet: the folder list with the
/// root on top, one selected destination, Cancel and Move. When a folder is moved, itself and
/// its descendants are not destinations (the core refuses a cycle anyway).
/// </summary>
public static class MoveSheet
{
    /// <summary>The chosen destination path, or null when the person cancelled.</summary>
    public static async Task<string?> ShowAsync(Page parent, IServiceProvider services, string? excludePath = null)
    {
        List<string> paths;
        using (var scope = services.CreateScope())
        {
            var folders = await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetAllActiveAsync();
            paths = folders.Select(f => ContentFormat.NormalizePath(f.Path))
                .Where(p => p != "/")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (excludePath is not null)
        {
            var prefix = ContentFormat.NormalizePath(excludePath).TrimEnd('/') + "/";
            paths = paths.Where(p => p != excludePath && !p.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;
        var theme = ThemeManager.Effective == AppTheme.Light ? "light" : "dark";
        var selected = "/";

        var list = new VerticalStackLayout { Spacing = 6 };
        var rows = new List<(string Path, Border Row)>();

        void Select(string path)
        {
            selected = path;
            foreach (var (p, row) in rows)
            {
                var active = p == path;
                row.BackgroundColor = ThemeManager.Token(active ? "AccentSoft" : "SurfaceHigh");
                row.Stroke = ThemeManager.Token(active ? "AccentBorder" : "Border");
            }
        }

        void AddRow(string path, string label)
        {
            var row = new Border
            {
                BackgroundColor = ThemeManager.Token("SurfaceHigh"),
                Stroke = ThemeManager.Token("Border"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12) },
                Padding = new Thickness(12, 10),
            };
            var content = new HorizontalStackLayout { Spacing = 10 };
            content.Children.Add(new Image
            {
                Source = $"icon_row_folder_{theme}.svg",
                WidthRequest = 18,
                HeightRequest = 18,
                VerticalOptions = LayoutOptions.Center,
            });
            content.Children.Add(new Label
            {
                Text = label,
                FontSize = 14,
                FontFamily = (string?)resources?["FontBodyMedium"],
                TextColor = ThemeManager.Token("Text"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            });
            row.Content = content;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Select(path);
            row.GestureRecognizers.Add(tap);
            rows.Add((path, row));
            list.Children.Add(row);
        }

        AddRow("/", AppStrings.Get("MoveRoot"));
        foreach (var path in paths) AddRow(path, path);
        Select("/");

        var scroll = new ScrollView { Content = list, MaximumHeightRequest = 240 };

        var buttons = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 10 };
        var cancelButton = new Button { Text = AppStrings.Get("Cancel"), Style = (Style?)resources?["SecondaryButton"] };
        var moveButton = new Button { Text = AppStrings.Get("Move"), Style = (Style?)resources?["PrimaryButton"] };
        Grid.SetColumn(cancelButton, 0);
        Grid.SetColumn(moveButton, 1);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(moveButton);

        var stack = new VerticalStackLayout { Spacing = 16 };
        stack.Children.Add(scroll);
        stack.Children.Add(buttons);

        var sheet = new BottomSheet.BottomSheetPage(stack, AppStrings.Get("MoveTitle"));
        // Close the sheet FIRST, then answer: the caller moves the article right after the answer.
        cancelButton.Clicked += async (_, _) =>
        {
            answered = true;
            await sheet.CloseAsync();
            chosen.TrySetResult(null);
        };
        moveButton.Clicked += async (_, _) =>
        {
            answered = true;
            await sheet.CloseAsync();
            chosen.TrySetResult(selected);
        };
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult(null); };

        await parent.Navigation.PushModalAsync(sheet);
        return await chosen.Task;
    }
}
