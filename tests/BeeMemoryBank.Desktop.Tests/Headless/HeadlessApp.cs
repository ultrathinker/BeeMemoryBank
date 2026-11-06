using Avalonia;
using Avalonia.Headless;
using BeeMemoryBank.Desktop;

namespace BeeMemoryBank.Desktop.Tests.Headless;

/// <summary>
/// Entry point of the headless Avalonia session the theme tests run in. It configures the real
/// desktop shell (its App.axaml, Fluent theme and DarkPalette styles) on the headless platform
/// with the Skia drawing backend, so what the tests measure is what a window would draw, not a
/// substitute style sheet.
/// </summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true
            })
            .WithInterFont();
}