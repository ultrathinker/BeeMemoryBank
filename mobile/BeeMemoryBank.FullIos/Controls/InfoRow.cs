using Microsoft.Maui.Layouts;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// A label + value row of the design's info cards ("Last sync ... 2026-11-14 21:05"). At a large
/// system font the caption can take the whole width, so a fixed two-column grid breaks the value
/// into several lines. This row measures both children and chooses: if the caption and the value
/// fit on one line, the caption sits on the left and the value on the right of that line; if they
/// do not fit, the value drops to its own line under the caption, left-aligned, and is only
/// wrapped when it is longer than the whole width. A small custom <see cref="ILayoutManager"/>
/// does the choosing; it re-measures on every width change, so rotation is handled. No FlexLayout
/// is involved. Copied from the Android app's InfoRow.
/// </summary>
[ContentProperty(nameof(Value))]
public class InfoRow : Layout
{
    /// <summary>Horizontal gap between the caption and the value when they share one line.</summary>
    private const double ColumnSpacing = 12;
    /// <summary>Vertical gap between the caption and the value when the value drops to its own line.</summary>
    private const double RowSpacing = 4;

    public static readonly BindableProperty LabelProperty =
        BindableProperty.Create(nameof(Label), typeof(string), typeof(InfoRow), "",
            propertyChanged: (b, _, n) => ((InfoRow)b).LabelView.Text = (string)n);

    public static readonly BindableProperty ValueProperty =
        BindableProperty.Create(nameof(Value), typeof(View), typeof(InfoRow), null,
            propertyChanged: (b, o, n) =>
            {
                var row = (InfoRow)b;
                if (o is View old) row.Children.Remove(old);
                if (n is View added) row.Children.Add(added);
            });

    /// <summary>The caption on the left (ItemMeta style).</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The value on the right (or on its own line when it does not fit).</summary>
    public View? Value
    {
        get => (View?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    internal Label LabelView { get; } = new();

    public InfoRow()
    {
        // TryGetValue, not the indexer: a missing key must never throw.
        if (Application.Current?.Resources.TryGetValue("ItemMeta", out var res) == true && res is Style meta)
            LabelView.Style = meta;
        Children.Add(LabelView);
    }

    protected override ILayoutManager CreateLayoutManager() => new InfoRowLayoutManager(this);

    private sealed class InfoRowLayoutManager : ILayoutManager
    {
        private readonly InfoRow _row;
        private bool _oneLine;
        private Size _labelSize;
        private Size _valueSize;

        public InfoRowLayoutManager(InfoRow row) => _row = row;

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            var padding = _row.Padding;
            double available = Math.Max(0, widthConstraint - padding.HorizontalThickness);
            var label = _row.LabelView;
            var value = _row.Value;

            // Desired (unwrapped) sizes tell whether the two fit on one line.
            var labelDesired = label.Measure(double.PositiveInfinity, double.PositiveInfinity);
            var valueDesired = value is null ? Size.Zero : value.Measure(double.PositiveInfinity, double.PositiveInfinity);
            _oneLine = value is null || labelDesired.Width + ColumnSpacing + valueDesired.Width <= available;

            double contentHeight;
            if (_oneLine)
            {
                _labelSize = labelDesired;
                _valueSize = valueDesired;
                contentHeight = Math.Max(labelDesired.Height, valueDesired.Height);
            }
            else
            {
                // Two lines: each child gets the full width, so a value wraps only if it is longer
                // than the whole row, never in the middle of a narrow right-hand column.
                _labelSize = label.Measure(available, double.PositiveInfinity);
                _valueSize = value!.Measure(available, double.PositiveInfinity);
                contentHeight = _labelSize.Height + RowSpacing + _valueSize.Height;
            }

            double desiredWidth = available + padding.HorizontalThickness;
            double width = double.IsInfinity(widthConstraint) ? desiredWidth : widthConstraint;
            return new Size(width, contentHeight + padding.VerticalThickness);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            var padding = _row.Padding;
            double x = bounds.X + padding.Left;
            double y = bounds.Y + padding.Top;
            double innerWidth = Math.Max(0, bounds.Width - padding.HorizontalThickness);
            double innerHeight = Math.Max(0, bounds.Height - padding.VerticalThickness);
            var label = _row.LabelView;
            var value = _row.Value;

            if (_oneLine)
            {
                double labelY = y + Math.Max(0, (innerHeight - _labelSize.Height) / 2);
                label.Arrange(new Rect(x, labelY, _labelSize.Width, _labelSize.Height));
                if (value is not null)
                {
                    double valueY = y + Math.Max(0, (innerHeight - _valueSize.Height) / 2);
                    double valueX = x + Math.Max(0, innerWidth - _valueSize.Width);
                    value.Arrange(new Rect(valueX, valueY, _valueSize.Width, _valueSize.Height));
                }
            }
            else
            {
                label.Arrange(new Rect(x, y, _labelSize.Width, _labelSize.Height));
                value?.Arrange(new Rect(x, y + _labelSize.Height + RowSpacing, _valueSize.Width, _valueSize.Height));
            }

            return new Size(bounds.Width, bounds.Height);
        }
    }
}
