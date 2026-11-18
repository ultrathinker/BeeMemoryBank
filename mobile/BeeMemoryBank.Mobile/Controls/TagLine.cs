using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The article's concept tags in ONE line: as many chips as fit, and when some do not fit a last
/// "+N" chip (N = how many are hidden) at the end of that line. Tapping "+N" raises
/// <see cref="ShowAllRequested"/> so the page can open a sheet with every tag. A small custom
/// <see cref="ILayoutManager"/> measures the chips and chooses how many fit; it re-measures on every
/// width change, so rotation is handled. When all the tags fit there is no "+N". The chips themselves
/// are not tappable (a tag chip opened nothing before, so it still opens nothing). No FlexLayout is
/// nested here.
/// </summary>
public class TagLine : Layout
{
    private const double Spacing = 8;

    private readonly List<string> _tags = new();
    private readonly List<View> _chips = new();
    private readonly Border _moreChip;
    private readonly Label _moreLabel;

    /// <summary>Raised when the "+N" chip is tapped; the page shows the sheet of all tags.</summary>
    public event EventHandler? ShowAllRequested;

    /// <summary>All the tags, in order, for the sheet the page opens.</summary>
    public IReadOnlyList<string> Tags => _tags;

    public TagLine()
    {
        _moreLabel = new Label { FontSize = 12, FontFamily = ResString("FontBodySemibold") };
        _moreChip = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
            Padding = new Thickness(10, 4),
            Content = _moreLabel,
        };
        ColorMoreChip();
        SemanticProperties.SetDescription(_moreChip, AppStrings.Get("ShowAllTags"));
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => ShowAllRequested?.Invoke(this, EventArgs.Empty);
        _moreChip.GestureRecognizers.Add(tap);
        Children.Add(_moreChip);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null && Application.Current is { } app)
            app.RequestedThemeChanged += OnRequestedThemeChanged;
        else if (Application.Current is { } gone)
            gone.RequestedThemeChanged -= OnRequestedThemeChanged;
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        ColorMoreChip();
        foreach (var chip in _chips)
            if (chip is Border border) ColorChip(border);
    }

    /// <summary>Rebuild the line for <paramref name="tags"/>; the layout re-fits on the next measure.</summary>
    public void SetTags(IEnumerable<string> tags)
    {
        _tags.Clear();
        _tags.AddRange(tags);
        foreach (var chip in _chips) Children.Remove(chip);
        _chips.Clear();
        foreach (var tag in _tags)
        {
            var chip = CreateChip(tag, new Thickness(0));
            _chips.Add(chip);
            // Keep the "+N" chip last so it is always at the end of the line.
            Children.Insert(Children.Count - 1, chip);
        }
        InvalidateMeasure();
    }

    /// <summary>A tag chip in the mock-up's style (.tag-pill): "#tag", surface-high, strong border.
    /// Used by the line and by the sheet of all tags, so a chip looks and behaves the same in both.</summary>
    public static View CreateChip(string tag, Thickness margin)
    {
        var label = new Label
        {
            Text = "#" + tag,
            FontSize = 12,
            FontFamily = ResString("FontBodySemibold"),
            // Centre the text vertically so a large system font does not leave it sitting high.
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var chip = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
            Padding = new Thickness(10, 4),
            Margin = margin,
            Content = label,
        };
        ColorChip(chip);
        return chip;
    }

    private static void ColorChip(Border chip)
    {
        chip.BackgroundColor = ThemeManager.Token("SurfaceHigh");
        chip.Stroke = ThemeManager.Token("Border");
        if (chip.Content is Label label) label.TextColor = ThemeManager.Token("TextSecondary");
    }

    private void ColorMoreChip()
    {
        _moreChip.BackgroundColor = ThemeManager.Token("SurfaceHigh");
        _moreChip.Stroke = ThemeManager.Token("Border");
        _moreLabel.TextColor = ThemeManager.Token("AccentText");
    }

    // TryGetValue, not the indexer: a missing key must never throw.
    private static string ResString(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is string s ? s : "";

    protected override ILayoutManager CreateLayoutManager() => new TagLineLayoutManager(this);

    private sealed class TagLineLayoutManager : ILayoutManager
    {
        private readonly TagLine _line;
        private double[] _widths = Array.Empty<double>();
        private int _fitCount;
        private bool _showMore;
        private double _moreWidth;
        private double _rowHeight;

        public TagLineLayoutManager(TagLine line) => _line = line;

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            var padding = _line.Padding;
            double available = Math.Max(0, widthConstraint - padding.HorizontalThickness);
            var chips = _line._chips;
            int n = chips.Count;
            if (n == 0)
            {
                _widths = Array.Empty<double>();
                _fitCount = 0;
                _showMore = false;
                _rowHeight = 0;
                return new Size(double.IsInfinity(widthConstraint) ? 0 : widthConstraint, padding.VerticalThickness);
            }

            _widths = new double[n];
            double chipHeight = 0;
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                var size = chips[i].Measure(double.PositiveInfinity, double.PositiveInfinity);
                _widths[i] = size.Width;
                chipHeight = Math.Max(chipHeight, size.Height);
                total += size.Width;
            }
            total += Spacing * (n - 1);

            if (total <= available)
            {
                _fitCount = n;
                _showMore = false;
                _rowHeight = chipHeight;
            }
            else
            {
                // Fit as many chips as possible while leaving room for the "+N" chip. The "+N" width
                // grows with the hidden count, so try the largest counts first.
                _fitCount = 0;
                _showMore = true;
                var prefixes = new double[n];
                double prefix = 0;
                for (int i = 0; i < n; i++) { prefix += _widths[i]; prefixes[i] = prefix; }
                for (int k = n - 1; k >= 1; k--)
                {
                    _line._moreLabel.Text = "+" + (n - k);
                    _moreWidth = _line._moreChip.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
                    double fitWidth = prefixes[k - 1] + Spacing * k + _moreWidth;
                    if (fitWidth <= available) { _fitCount = k; break; }
                }
                if (_fitCount == 0)
                {
                    _line._moreLabel.Text = "+" + n;
                    _moreWidth = _line._moreChip.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
                }
                _rowHeight = Math.Max(chipHeight, _line._moreChip.DesiredSize.Height);
            }

            double width = double.IsInfinity(widthConstraint) ? total + padding.HorizontalThickness : widthConstraint;
            return new Size(width, _rowHeight + padding.VerticalThickness);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            var padding = _line.Padding;
            double x = bounds.X + padding.Left;
            double y = bounds.Y + padding.Top;
            var chips = _line._chips;

            double xPos = x;
            for (int i = 0; i < chips.Count; i++)
            {
                if (i < _fitCount)
                {
                    chips[i].Opacity = 1;
                    chips[i].Arrange(new Rect(xPos, y, _widths[i], _rowHeight));
                    xPos += _widths[i] + Spacing;
                }
                else
                {
                    // Hide overflow chips: a 0x0 arrange still draws the rounded border as a tiny ring.
                    // Opacity 0 (not IsVisible) keeps them measurable for the next width change (rotation).
                    chips[i].Opacity = 0;
                    chips[i].Arrange(new Rect(0, 0, 0, 0));
                }
            }

            if (_showMore)
            {
                _line._moreChip.Opacity = 1;
                _line._moreChip.Arrange(new Rect(xPos, y, _moreWidth, _rowHeight));
            }
            else
            {
                _line._moreChip.Opacity = 0;
                _line._moreChip.Arrange(new Rect(0, 0, 0, 0));
            }

            return new Size(bounds.Width, bounds.Height);
        }
    }
}
