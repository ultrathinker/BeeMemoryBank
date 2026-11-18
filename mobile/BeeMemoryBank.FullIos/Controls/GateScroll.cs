namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The gate pages' rule: centre the card when it fits, scroll when it does not (landscape, big
/// text). A ScrollView alone cannot do both - centring its content clips the overflow - so the
/// content is a Grid at least as tall as the viewport (the card centres inside it) and taller
/// than the viewport when the card needs more room (then the ScrollView scrolls). Display-only:
/// it measures the viewport, it changes nothing the app does.
/// </summary>
public static class GateScroll
{
    /// <summary>Keeps <paramref name="content"/> (a Grid with the card centred in it) at least as tall as the scroll viewport.</summary>
    public static void Attach(ScrollView scroll, View content)
    {
        scroll.SizeChanged += (_, _) =>
        {
            if (scroll.Height > 0)
                content.MinimumHeightRequest = scroll.Height;
        };
    }
}
