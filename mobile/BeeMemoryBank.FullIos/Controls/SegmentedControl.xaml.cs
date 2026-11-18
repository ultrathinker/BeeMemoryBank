namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The design's segmented control: two or three choices in one rounded row (the theme picker of
/// Settings). Set <see cref="Items"/> and read or bind <see cref="SelectedIndex"/>;
/// <see cref="SelectionChanged"/> tells the page a choice was tapped. Copied from the Android
/// app, with the device-check rule applied: a segment stays on one line and gets its text's full
/// width - the widest measured caption sets the equal segment width, so none is ever cut.
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
        // iOS measures a button only once its handler is attached, so the equal segment width is
        // fitted when the control is loaded and laid out (and again if the items change shown).
        Loaded += (_, _) => FitSegments();
        SizeChanged += (_, _) => FitSegments();
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
            var index = i;
            var button = new Button
            {
                Text = items[i],
                FontSize = 14,
                FontFamily = ResString("FontBodySemibold"),
                HeightRequest = 40,
                CornerRadius = 10,
                Padding = new Thickness(4, 6),
                // A segment is a quiet chip, not a full button: no border of its own, one line.
                BorderWidth = 0,
                BackgroundColor = Colors.Transparent,
                LineBreakMode = LineBreakMode.NoWrap,
                HorizontalOptions = LayoutOptions.Fill,
            };
            button.Clicked += (_, _) =>
            {
                if (SelectedIndex == index) return;
                SelectedIndex = index;
                SelectionChanged?.Invoke(this, index);
            };
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(button, i);
            _buttons.Add(button);
            SegmentsGrid.Children.Add(button);
        }
        UpdateVisuals();
        if (Handler is not null) FitSegments();
    }

    /// <summary>
    /// Equal segments, each at least its text's full width: the widest measured caption sets the
    /// segment width, so no segment ever cuts or wraps its caption (the device-check rule). The
    /// buttons measure for real only with their handlers attached, so this runs once loaded.
    /// </summary>
    private void FitSegments()
    {
        if (_buttons.Count == 0 || _buttons.Count != SegmentsGrid.ColumnDefinitions.Count) return;
        double segmentWidth = 0;
        foreach (var button in _buttons)
            segmentWidth = Math.Max(segmentWidth, button.Measure(double.PositiveInfinity, double.PositiveInfinity).Width);
        if (segmentWidth <= 0) return;
        // Inner horizontal room on top of the button's own padding, so even at a large system
        // font the caption never touches the pill's edges.
        segmentWidth += 12;
        for (var i = 0; i < SegmentsGrid.ColumnDefinitions.Count; i++)
            SegmentsGrid.ColumnDefinitions[i].Width = new GridLength(segmentWidth, GridUnitType.Absolute);
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
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is string s ? s : "";
}
