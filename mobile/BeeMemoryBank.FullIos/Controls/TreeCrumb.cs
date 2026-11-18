using BeeMemoryBank.FullIos.Localization;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// One crumb of the design's breadcrumb rail (.crumb-btn): a small rounded chip, the current
/// one in the accent colours (.is-current), the root one with the home icon. A crumb with no
/// tap action is the current folder and does nothing. Copied from the Android app's TreeCrumb;
/// iOS names the rasterized icons .png.
/// </summary>
public static class TreeCrumb
{
    public static View Build(string text, bool isCurrent, Action? onTap, bool isRoot = false)
    {
        var resources = Application.Current?.Resources;

        var content = new HorizontalStackLayout { Spacing = 6 };
        if (isRoot)
        {
            content.Children.Add(new Image
            {
                Source = ThemeManager.Icon("icon_row_home"),
                WidthRequest = 15,
                HeightRequest = 15,
                VerticalOptions = LayoutOptions.Center,
            });
        }
        content.Children.Add(new Label
        {
            Text = text,
            FontSize = 13,
            FontFamily = ResString(resources, isCurrent ? "FontBodySemibold" : "FontBodyMedium"),
            TextColor = ThemeManager.Token(isCurrent ? "AccentText" : "TextSecondary"),
            VerticalOptions = LayoutOptions.Center,
        });

        var crumb = new Border
        {
            BackgroundColor = ThemeManager.Token(isCurrent ? "AccentSoft" : "Surface"),
            Stroke = ThemeManager.Token(isCurrent ? "AccentBorder" : "Border"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(10) },
            Padding = new Thickness(10, 6),
            MinimumHeightRequest = 36,
            Content = content,
        };

        if (onTap is not null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onTap();
            crumb.GestureRecognizers.Add(tap);
        }
        return crumb;
    }

    // TryGetValue, not the indexer: a missing key must never throw.
    private static string ResString(ResourceDictionary? resources, string key)
        => resources is not null && resources.TryGetValue(key, out var v) == true && v is string s ? s : "";
}
