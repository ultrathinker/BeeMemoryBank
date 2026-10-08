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

    public static string Render(string markdown) => Page(Markdown.ToHtml(markdown ?? "", Pipeline));

    /// <summary>A plain-text page (a message instead of a note), escaped.</summary>
    public static string Text(string text) => Page($"<p>{WebUtility.HtmlEncode(text ?? "")}</p>");

    private static string Page(string body) =>
        $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{ContentSecurityPolicy}}">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <style>{{Style}}</style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;

    private const string Style = """
        :root { color-scheme: light dark; --text: #1c1017; --muted: #6e5a64; --accent: #c2185b; --code: #f5f0f2; --border: #e3d7dd; }
        @media (prefers-color-scheme: dark) { :root { --text: #e8d8df; --muted: #a88a97; --accent: #f06292; --code: #2a1520; --border: #4a2838; } }
        html { -webkit-text-size-adjust: 100%; }
        body { font: -apple-system-body; color: var(--text); background: transparent; margin: 0; padding: 4px 2px 48px; word-wrap: break-word; }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.2em 0 0.5em; }
        h1 { font-size: 1.5em; } h2 { font-size: 1.3em; } h3 { font-size: 1.15em; }
        a { color: var(--accent); }
        pre, code { font-family: ui-monospace, Menlo, monospace; font-size: 0.9em; background: var(--code); border-radius: 6px; }
        code { padding: 0.1em 0.3em; }
        pre { padding: 10px; overflow-x: auto; }
        pre code { padding: 0; background: none; }
        blockquote { margin: 0.8em 0; padding: 0 0 0 12px; border-left: 3px solid var(--border); color: var(--muted); }
        table { border-collapse: collapse; display: block; overflow-x: auto; }
        th, td { border: 1px solid var(--border); padding: 4px 8px; }
        img { max-width: 100%; }
        hr { border: none; border-top: 1px solid var(--border); }
        ul.contains-task-list { padding-left: 1.2em; } li.task-list-item { list-style: none; }
        """;
}
