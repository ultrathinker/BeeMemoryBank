using System.Net;
using Markdig;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// A note's Markdown as the page the note view shows. Rendered on the phone (Markdig, as the Android app), with raw HTML in a note shown
/// as text rather than run, and a Content-Security-Policy that lets the page load nothing at all: no script, no remote picture, font or
/// style - so opening a note never tells anyone on the network that it was opened, and a note cannot carry code. Links are not followed
/// inside the page; the note page hands them to the system browser after asking.
/// </summary>
public static class NoteHtml
{
    /// <summary>The policy of every note page: only its own inline style, and pictures embedded in the page itself.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>
    /// The note's page. <paramref name="dark"/> forces the appearance the app shows (the design's
    /// tokens); null follows the system's prefers-color-scheme, as before. The Markdown handling
    /// and the policy are the same whatever the appearance.
    /// </summary>
    public static string Render(string markdown, bool? dark = null) => Page(Markdown.ToHtml(markdown ?? "", Pipeline), dark);

    /// <summary>A plain-text page (a message instead of a note), escaped.</summary>
    public static string Text(string text, bool? dark = null) => Page($"<p>{WebUtility.HtmlEncode(text ?? "")}</p>", dark);

    private static string Page(string body, bool? dark) =>
        $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{ContentSecurityPolicy}}">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="{{(dark is null ? "light dark" : dark.Value ? "dark" : "light")}}">
            <style>{{StyleFor(dark)}}</style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;

    // The design's tokens (Resources/Styles/Colors.xaml), as CSS: the note reads in the same
    // palette as the app around it. The body keeps the system text style so Dynamic Type carries
    // into the note; the WebView cannot load the app's faces under the page's own policy.
    private const string LightTokens = "--text: #14171F; --muted: #61697D; --accent: #8A6000; --code: #ECEEF4; --border: rgba(20,23,31,0.22);";
    private const string DarkTokens = "--text: #F3F5F8; --muted: #848C9E; --accent: #FFC800; --code: #1E222E; --border: rgba(255,255,255,0.22);";

    private static string StyleFor(bool? dark)
    {
        var root = dark switch
        {
            true => $":root {{ color-scheme: dark; {DarkTokens} }}",
            false => $":root {{ color-scheme: light; {LightTokens} }}",
            _ => $":root {{ color-scheme: light dark; {LightTokens} }}\n@media (prefers-color-scheme: dark) {{ :root {{ {DarkTokens} }} }}",
        };
        return root + "\n" + Style;
    }

    private const string Style = """
        html { -webkit-text-size-adjust: 100%; }
        /* The note page is one ScrollView: the WebView is sized to this content and never
           scrolls inside itself, so no tab-bar room is needed here (the page carries it). */
        html, body { overflow: hidden; }
        body { font: -apple-system-body; color: var(--text); background: transparent; margin: 0; padding: 4px 0 8px; word-wrap: break-word; }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.2em 0 0.5em; font-weight: 700; }
        h1 { font-size: 1.5em; } h2 { font-size: 1.3em; } h3 { font-size: 1.15em; }
        a { color: var(--accent); }
        pre, code { font-family: ui-monospace, Menlo, monospace; font-size: 0.9em; background: var(--code); border-radius: 6px; }
        code { padding: 0.1em 0.3em; }
        pre { padding: 10px; overflow-x: auto; }
        pre code { padding: 0; background: none; }
        blockquote { margin: 0.8em 0; padding: 0 0 0 12px; border-left: 3px solid var(--border); color: var(--muted); }
        table { border-collapse: collapse; display: block; overflow-x: auto; }
        th, td { border: 1px solid var(--border); padding: 4px 8px; }
        /* A picture on its own line: inline after text it made one tall broken line (real iPhone, 15.11). */
        img { display: block; max-width: 100%; height: auto; margin: 0.6em 0; border-radius: 8px; }
        hr { border: none; border-top: 1px solid var(--border); }
        ul.contains-task-list { padding-left: 1.2em; } li.task-list-item { list-style: none; }
        """;
}
