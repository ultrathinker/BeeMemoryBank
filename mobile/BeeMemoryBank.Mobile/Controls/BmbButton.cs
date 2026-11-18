namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// Marker for buttons that must stay on ONE line (the segmented control's segments). The multi-line
/// Button mapper in MauiProgram wraps every button so a long caption is never cut; a button carrying
/// this marker is kept single-line instead and is sized to its text rather than squeezed. The marker
/// is an attached property, not the caption text, so it is independent of language and wording.
/// </summary>
public static class BmbButton
{
    public static readonly BindableProperty SingleLineProperty =
        BindableProperty.CreateAttached("SingleLine", typeof(bool), typeof(BmbButton), false);

    public static bool GetSingleLine(BindableObject bindable) => (bool)bindable.GetValue(SingleLineProperty);

    public static void SetSingleLine(BindableObject bindable, bool value) => bindable.SetValue(SingleLineProperty, value);
}
