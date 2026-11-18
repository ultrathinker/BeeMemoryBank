using Microsoft.Maui.Layouts;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// A multi-line wrap layout: every child is measured at its natural (unlimited) width and WHOLE
/// children are packed into rows, wrapping to the next row when one is full. A child is never shrunk
/// to fit a row, so a chip's text is never cut or broken (a FlexLayout squeezed the all-tags sheet's
/// first row into "#alph", "#android-redesig"). A small custom <see cref="ILayoutManager"/>, like
/// <see cref="TagLine"/> but multi-line and keeping every child. No FlexLayout is involved.
/// Copied from the Android app's WrapLayout (its own namespace here; the boundary tests pin the
/// linked-source list, so code is copied, not linked).
/// </summary>
public class WrapLayout : Layout
{
    /// <summary>Horizontal and vertical gap between children.</summary>
    public double Spacing { get; set; } = 8;

    protected override ILayoutManager CreateLayoutManager() => new WrapLayoutManager(this);

    private sealed class WrapLayoutManager : ILayoutManager
    {
        private readonly WrapLayout _layout;
        private readonly List<Size> _sizes = new();
        private readonly List<Point> _positions = new();
        private double _contentHeight;

        public WrapLayoutManager(WrapLayout layout) => _layout = layout;

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            var padding = _layout.Padding;
            double available = Math.Max(0, widthConstraint - padding.HorizontalThickness);
            _sizes.Clear();
            _positions.Clear();

            double x = 0, y = 0, rowHeight = 0, maxRowWidth = 0;
            foreach (var child in _layout.Children)
            {
                // Unlimited width: a chip measures its whole text, never a squeezed fragment.
                var size = child.Measure(double.PositiveInfinity, double.PositiveInfinity);
                _sizes.Add(size);
                if (x > 0 && x + size.Width > available)
                {
                    maxRowWidth = Math.Max(maxRowWidth, x - _layout.Spacing);
                    x = 0;
                    y += rowHeight + _layout.Spacing;
                    rowHeight = 0;
                }
                _positions.Add(new Point(x, y));
                x += size.Width + _layout.Spacing;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            maxRowWidth = Math.Max(maxRowWidth, x - _layout.Spacing);
            _contentHeight = y + rowHeight;

            double width = double.IsInfinity(widthConstraint)
                ? Math.Max(0, maxRowWidth) + padding.HorizontalThickness
                : widthConstraint;
            return new Size(width, _contentHeight + padding.VerticalThickness);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            var padding = _layout.Padding;
            double ox = bounds.X + padding.Left;
            double oy = bounds.Y + padding.Top;
            for (int i = 0; i < _layout.Children.Count; i++)
            {
                var pos = _positions[i];
                var size = _sizes[i];
                _layout.Children[i].Arrange(new Rect(ox + pos.X, oy + pos.Y, size.Width, size.Height));
            }
            return new Size(bounds.Width, bounds.Height);
        }
    }
}
