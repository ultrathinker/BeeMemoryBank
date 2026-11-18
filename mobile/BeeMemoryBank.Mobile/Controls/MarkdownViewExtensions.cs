using Indiko.Maui.Controls.Markdown;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The article body wears the app's fonts, not the system Roboto. Indiko's MarkdownView exposes
/// font-face properties (assigned to MAUI Labels, so the app's font aliases resolve); set them after
/// the theme, whose typography would otherwise leave the default system face. Body is NotoSans, the
/// headings the display face, code stays monospace.
/// </summary>
public static class MarkdownViewExtensions
{
    public static void ApplyAppFonts(this MarkdownView view)
    {
        var body = Font("FontBody");
        // Bold like the article title: on the device the semibold face read as regular weight.
        var heading = Font("FontDisplayBold");
        view.TextFontFace = body;
        view.H1FontFamily = heading;
        view.H2FontFamily = heading;
        view.H3FontFamily = heading;
        view.H4FontFamily = heading;
        view.H5FontFamily = heading;
        view.H6FontFamily = heading;
        view.TableHeaderFontFace = Font("FontBodySemibold");
        view.TableRowFontFace = body;
        view.CodeBlockFontFace = "monospace";
        view.BlockQuoteFontFace = body;
    }

    // TryGetValue, not the indexer: a missing key must never throw.
    private static string Font(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is string s ? s : "";
}
