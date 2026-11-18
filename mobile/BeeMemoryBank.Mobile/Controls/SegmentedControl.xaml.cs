using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The mock-up's segmented control: two or three equal choices in one rounded row (the theme
/// picker of Settings, the Text/Preview switch of the editor). Set <see cref="Items"/> and read
/// or bind <see cref="SelectedIndex"/>; <see cref="SelectionChanged"/> tells the page a choice
/// was tapped.
/// </summary>
public partial class SegmentedControl : ContentView
{
    public static readonly BindableProperty ItemsProperty =
        BindableProperty.Create(nameof(Items), typeof(IReadOnlyList<string>), typeof(SegmentedControl), null,
            propertyChanged: (b, _, _) => ((SegmentedControl)b).Rebuild());

    public static readonly BindableProperty SelectedIndexProperty =
        BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(SegmentedControl), 0, BindingMode.TwoWay,
            propertyChanged: (b, _, _) => ((SegmentedControl)b).UpdateVisuals());

    public IReadOnlyList<string>? Items
    {
        get => (IReadOnlyList<string>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Raised with the new index when the person taps a segment (not on a programmatic change).</summary>
    public event EventHandler<int>? SelectionChanged;

    private readonly List<Button> _buttons = [];

    public SegmentedControl()
    {
        InitializeComponent();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null && Application.Current is { } app)
            app.RequestedThemeChanged += OnRequestedThemeChanged;
        else if (Application.Current is { } gone)
            gone.RequestedThemeChanged -= OnRequestedThemeChanged;
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateVisuals();

    private void Rebuild()
    {
        SegmentsGrid.Children.Clear();
        SegmentsGrid.ColumnDefinitions.Clear();
        _buttons.Clear();

        var items = Items;
        if (items is null || items.Count == 0) return;

        for (var i = 0; i < items.Count; i++)
        {
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var index = i;
            var button = new Button
            {
                Text = items[i],
                FontSize = 14,
                FontFamily = ResString("FontBodySemibold"),
                HeightRequest = 40,
                CornerRadius = 10,
                Padding = new Thickness(4, 6),
                // A segment is a quiet chip, not a full button: no border of its own.
                BorderWidth = 0,
                BackgroundColor = Colors.Transparent,
            };
            // A segment stays on one line; the control is sized to its text, so the multi-line
            // Button mapper must not wrap it (a large system font broke the Russian "Preview" label into two lines).
            BmbButton.SetSingleLine(button, true);
            button.Clicked += (_, _) =>
            {
                if (SelectedIndex == index) return;
                SelectedIndex = index;
                SelectionChanged?.Invoke(this, index);
            };
            Grid.SetColumn(button, i);
            _buttons.Add(button);
            SegmentsGrid.Children.Add(button);
        }
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var activeBackground = ThemeManager.Token("SurfaceHigh");
        var activeText = ThemeManager.Token("AccentText");
        var idleText = ThemeManager.Token("TextSecondary");

        for (var i = 0; i < _buttons.Count; i++)
        {
            var active = i == SelectedIndex;
            _buttons[i].BackgroundColor = active ? activeBackground : Colors.Transparent;
            _buttons[i].TextColor = active ? activeText : idleText;
        }
    }

    // TryGetValue, not the indexer: a missing key must never throw.
    private static string ResString(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is string s ? s : "NotoSansSemibold";
}
