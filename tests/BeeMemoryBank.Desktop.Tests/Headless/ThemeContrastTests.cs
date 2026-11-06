using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeeMemoryBank.Desktop.Views;

namespace BeeMemoryBank.Desktop.Tests.Headless;

/// <summary>
/// Renders the "New profile" dialog (CreateStorageDialog) under BOTH theme variants and measures
/// the colours the controls actually draw with, in the states the tester's report names: idle,
/// focused and pointer-over. The four bugs of BMB-334 were all one root cause - a dark hard-coded
/// palette under a theme that followed the system - so every text surface of the dialog is checked
/// against its own painted background at WCAG's 4.5:1 ratio, and a hovered button must still have
/// a background that differs from its text colour.
///
/// <para>This is deliberately a render test, not a resource test: the old bug had the TextBox's
/// Background property saying #1E1E2A while its template border painted white on focus. Reading
/// the visual tree after the styles ran measures what is on screen.</para>
/// </summary>
public class ThemeContrastTests
{
    private const double MinimumContrast = 4.5;

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task NewProfileDialog_IsReadableInEveryState(string variant) =>
        HeadlessSession.RunAsync(() => RunScenario(variant));

    private static void RunScenario(string variant)
    {
        var app = Application.Current ?? throw new InvalidOperationException("No Avalonia application was set up.");
        app.RequestedThemeVariant = variant == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        var dialog = new CreateStorageDialog();
        try
        {
            dialog.Show();
            Settle();

            var nameBox = Find<TextBox>(dialog, "NameBox");
            var cancelButton = Find<Button>(dialog, "CancelButton");
            var createButton = Find<Button>(dialog, "CreateButton");
            var advanced = Find<Expander>(dialog, "AdvancedExpander");

            // ---- idle (the dialog opens focused on NameBox; put focus on Cancel so "idle" is idle) ----
            cancelButton.Focus();
            Settle();
            AssertSkinned(nameBox, "NameBox (idle)", variant);
            AssertContrast(ButtonTextColour(cancelButton), ButtonBackground(cancelButton),
                $"{variant}: Cancel button (idle)", dialog);
            AssertContrast(ButtonTextColour(createButton), ButtonBackground(createButton),
                $"{variant}: Create button (idle)", dialog);
            SaveShot(dialog, variant, "idle");

            // ---- focused, with text typed in ----
            nameBox.Focus();
            nameBox.Text = "Work";
            Settle();
            AssertSkinned(nameBox, "NameBox (focused, text typed)", variant);
            AssertContrast(TextPresenterColour(nameBox), BackgroundOf(nameBox),
                $"{variant}: NameBox typed text (focused)", dialog);
            SaveShot(dialog, variant, "focused");

            // ---- pointer over the two buttons that "vanished" ----
            nameBox.Text = "";
            Hover(dialog, cancelButton);
            var cancelBackground = ButtonBackground(cancelButton);
            AssertContrast(ButtonTextColour(cancelButton), cancelBackground,
                $"{variant}: Cancel button (pointer over)", dialog);
            cancelBackground.Should().NotBe(ButtonTextColour(cancelButton),
                $"{variant}: a hovered Cancel button must keep a background distinct from its text");
            SaveShot(dialog, variant, "hover-cancel");

            Hover(dialog, createButton);
            var createBackground = ButtonBackground(createButton);
            AssertContrast(ButtonTextColour(createButton), createBackground,
                $"{variant}: Create button (pointer over)", dialog);
            createBackground.Should().NotBe(ButtonTextColour(createButton),
                $"{variant}: a hovered Create button must keep a background distinct from its text");
            SaveShot(dialog, variant, "hover-create");

            // ---- Advanced > Data folder: the second placeholder the report names ----
            advanced.IsExpanded = true;
            Settle();
            var dataPathBox = Find<TextBox>(dialog, "DataPathBox");
            AssertSkinned(dataPathBox, "DataPathBox (idle)", variant);
            dataPathBox.Focus();
            Settle();
            AssertSkinned(dataPathBox, "DataPathBox (focused)", variant);
            SaveShot(dialog, variant, "advanced");
        }
        finally
        {
            dialog.Close();
            Settle();
        }
    }

    private static void AssertSkinned(TextBox box, string what, string variant)
    {
        var background = BackgroundOf(box);
        var placeholder = PlaceholderColour(box, what, variant);
        AssertContrast(placeholder, background, $"{variant}: {what} placeholder", box);
        AssertContrast(TextPresenterColour(box), background, $"{variant}: {what} text", box);
    }

    private static void AssertContrast(Color foreground, Color background, string what, Control context)
    {
        var ratio = ContrastRatio(foreground, background);
        if (ratio < MinimumContrast)
            throw new Xunit.Sdk.XunitException(
                $"{what}: contrast {ratio:F2}:1 is below {MinimumContrast}:1 " +
                $"(text {Describe(foreground)} on {Describe(background)}).\n\nVisual tree:\n{DumpTree(context)}");
    }

    private static void Hover(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("The control is not in this window.");
        window.MouseMove(centre);
        Settle();
        // The render tick is what makes the pointer-over pseudo-class visible to the style system.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    private static Color BackgroundOf(TextBox box)
    {
        var border = box.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_BorderElement")
            ?? throw new InvalidOperationException(
                "The TextBox template has no Border named PART_BorderElement.\n\nVisual tree:\n" + DumpTree(box));

        var colour = ToColour(border.Background, box);
        return colour;
    }

    private static Color PlaceholderColour(TextBox box, string what, string variant)
    {
        var placeholder = box.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => !string.IsNullOrEmpty(box.PlaceholderText) && t.Text == box.PlaceholderText);
        if (placeholder is not null && ToColourOrNull(placeholder.Foreground) is { } colour && colour.A > 0)
            return colour;

        if (ToColourOrNull(box.PlaceholderForeground) is { } own && own.A > 0)
            return own;

        throw new InvalidOperationException(
            $"{variant}: {what}: no placeholder TextBlock or PlaceholderForeground could be found.\n\nVisual tree:\n{DumpTree(box)}");
    }

    private static Color TextPresenterColour(TextBox box)
    {
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()
            ?? throw new InvalidOperationException("The TextBox template has no TextPresenter.\n\nVisual tree:\n" + DumpTree(box));
        return ToColour(presenter.Foreground, box);
    }

    private static Color ButtonBackground(Button button)
    {
        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter")
            ?? throw new InvalidOperationException(
                "The Button template has no ContentPresenter named PART_ContentPresenter.\n\nVisual tree:\n" + DumpTree(button));
        return ToColour(presenter.Background, button);
    }

    private static Color ButtonTextColour(Button button)
    {
        // The presenter generates a TextBlock for the string Content; its Foreground is what is drawn.
        var text = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text));
        if (text is not null && ToColourOrNull(text.Foreground) is { } colour && colour.A > 0)
            return colour;

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter");
        if (presenter is not null && ToColourOrNull(presenter.Foreground) is { } presented && presented.A > 0)
            return presented;

        return ToColour(button.Foreground, button);
    }

    private static Color ToColour(IBrush? brush, Control context)
    {
        if (ToColourOrNull(brush) is { } colour)
            return colour;
        throw new InvalidOperationException("A control draws no colour.\n\nVisual tree:\n" + DumpTree(context));
    }

    private static Color? ToColourOrNull(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        null => null,
        _ => null
    };

    /// <summary>WCAG 2.x relative luminance contrast ratio; alpha is composited over the window colour first.</summary>
    private static double ContrastRatio(Color foreground, Color background)
    {
        var window = Color.Parse("#111119");
        var bg = Composite(background, window);
        var fg = Composite(foreground, bg);

        var l1 = Luminance(fg);
        var l2 = Luminance(bg);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static Color Composite(Color top, Color bottom)
    {
        if (top.A == 255) return top;
        var a = top.A / 255.0;
        return Color.FromArgb(255,
            (byte)Math.Round(top.R * a + bottom.R * (1 - a)),
            (byte)Math.Round(top.G * a + bottom.G * (1 - a)),
            (byte)Math.Round(top.B * a + bottom.B * (1 - a)));
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }

    private static string Describe(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2} (alpha {colour.A})";

    private static T Find<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"'{name}' was not found in {root.GetType().Name}.");

    private static void SaveShot(Window window, string variant, string state)
    {
        var directory = Environment.GetEnvironmentVariable("BMB_THEME_SHOTS_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, $"new-profile-{variant.ToLowerInvariant()}-{state}.png"));
    }

    private static string DumpTree(Visual root)
    {
        var builder = new StringBuilder();
        void Walk(Visual visual, int depth)
        {
            var control = visual as Control;
            var colours = visual switch
            {
                TemplatedControl templated => $" fg={DescribeBrush(templated.Foreground)} bg={DescribeBrush(templated.Background)}",
                TextBlock text => $" fg={DescribeBrush(text.Foreground)}",
                Border border => $" bg={DescribeBrush(border.Background)}",
                _ => ""
            };
            builder.AppendLine($"{new string(' ', depth * 2)}{visual.GetType().Name} name='{control?.Name}'{colours}");
            foreach (var child in visual.GetVisualChildren())
                Walk(child, depth + 1);
        }

        Walk(root, 0);
        return builder.ToString();
    }

    private static string DescribeBrush(IBrush? brush) => ToColourOrNull(brush) is { } colour
        ? Describe(colour)
        : brush?.GetType().Name ?? "null";
}