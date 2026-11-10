using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Tolerant reading of a model reply: strip a code fence (and a reasoning block), take the first '{' to the last '}',
/// parse it as one JSON object, then the schema check through <see cref="DreamJsonFields"/> (types, enumerations,
/// lengths; ids and paths only from the input set, by the task that knows the set), plan 3.7. A reply is untrusted
/// input: nothing here follows anything the text says, and every error message names a field, never a value.
/// Shared by every model task (pair_verdict now; folder_review and tree_review read their replies the same way).
/// </summary>
public static class DreamJsonReader
{
    /// <summary>A reply longer than this is not read at all (the output limit keeps real ones far below it).</summary>
    public const int MaxReplyChars = 100_000;

    /// <summary>The journal's word for a reply the output limit stopped (<see cref="DreamModelReply.CutOff"/>), which is
    /// a different thing from a reply that is empty or has no JSON in it.</summary>
    public const string CutOffError = "reply cut off by the output limit";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 12,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Regex Fence = new(@"```[ \t]*[A-Za-z]*[ \t]*\r?\n?(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Reasoning = new(@"<think(?:ing)?>.*?</think(?:ing)?>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Reads the one JSON object a reply carries.</summary>
    /// <param name="error">On failure, what was wrong, for the model's one retry and for the journal: no value of the
    /// reply is in it.</param>
    public static bool TryReadObject(string? reply, [NotNullWhen(true)] out JsonObject? obj, [NotNullWhen(false)] out string? error)
    {
        obj = null;
        if (string.IsNullOrWhiteSpace(reply))
        {
            error = "The reply is empty.";
            return false;
        }
        if (reply.Length > MaxReplyChars)
        {
            error = "The reply is too long.";
            return false;
        }

        string text;
        string? fenced;
        try
        {
            text = Reasoning.Replace(reply, " ");
            fenced = Fence.Match(text) is { Success: true } m ? m.Groups["body"].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            error = "The reply could not be read.";
            return false;
        }

        error = "The reply holds no JSON object.";
        string[] candidates = fenced == null ? [text] : [fenced, text];
        foreach (var candidate in candidates)
        {
            var start = candidate.IndexOf('{');
            var end = candidate.LastIndexOf('}');
            if (start < 0 || end < start) continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(candidate.AsSpan(start, end - start + 1).ToString(), null, ParseOptions);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                error = "The reply is not valid JSON.";
                continue;
            }

            if (node is JsonObject o)
            {
                obj = o;
                error = null;
                return true;
            }
            error = "The reply is not a JSON object.";
        }
        return false;
    }

    /// <summary>
    /// Text from a model made safe to keep and show: control characters (new lines included) become single spaces, the
    /// invisible direction controls that can make text read backwards are dropped, runs of white space collapse, the
    /// ends are trimmed, and what is longer than <paramref name="maxLength"/> is cut there with an ellipsis (the whole
    /// result is never longer than the limit).
    /// </summary>
    public static string CleanText(string text, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 8));
        var space = false;
        foreach (var c in text)
        {
            if (IsDirectionControl(c)) continue;
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
        }
        return Cut(sb.ToString(), maxLength);
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> characters, ending in an ellipsis
    /// when something was cut, and never in the middle of a surrogate pair.</summary>
    public static string Cut(string text, int maxLength)
    {
        if (maxLength <= 0) return "";
        if (text.Length <= maxLength) return text;
        var keep = maxLength - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep].TrimEnd() + "\u2026";
    }

    private static bool IsDirectionControl(char c) =>
        c is '\u200e' or '\u200f' or '\u061c' || (c >= '\u202a' && c <= '\u202e') || (c >= '\u2066' && c <= '\u2069');
}

/// <summary>
/// The schema check of one JSON object of a reply. Each reader returns the value or null and, when the field is
/// present but wrong (or required and missing), records the FIRST problem in <see cref="Error"/> and nothing after it:
/// the task reads its fields, then looks at <see cref="Error"/> once. Unknown fields are ignored. The messages name
/// the field and the rule, never the model's value.
/// </summary>
public sealed class DreamJsonFields(JsonObject obj)
{
    public JsonObject Object { get; } = obj;

    /// <summary>The first problem found, or null.</summary>
    public string? Error { get; private set; }

    public bool Ok => Error == null;

    /// <summary>Records a problem found by the task itself (an id outside the input set, say).</summary>
    public void Fail(string message) => Error ??= message;

    /// <summary>A string, cleaned and cut by <see cref="DreamJsonReader.CleanText"/>. Null (no problem recorded) when
    /// the field is absent or null and not required.</summary>
    public string? Text(string name, int maxLength, bool required)
    {
        var node = Get(name);
        if (node == null)
        {
            if (required) Fail($"'{name}' is required.");
            return null;
        }
        if (node is not JsonValue v || !v.TryGetValue<string>(out var raw))
        {
            Fail($"'{name}' must be a string.");
            return null;
        }
        var text = DreamJsonReader.CleanText(raw, maxLength);
        if (required && text.Length == 0)
        {
            Fail($"'{name}' must not be empty.");
            return null;
        }
        return text.Length == 0 ? null : text;
    }

    /// <summary>A string equal, ignoring case and outer white space, to one of <paramref name="allowed"/>; returns the
    /// allowed spelling.</summary>
    public string? OneOf(string name, IReadOnlyCollection<string> allowed, bool required)
    {
        var node = Get(name);
        if (node == null)
        {
            if (required) Fail($"'{name}' is required.");
            return null;
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var raw))
        {
            var trimmed = raw.Trim();
            foreach (var a in allowed)
                if (string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase)) return a;
        }
        Fail($"'{name}' must be one of: {string.Join(", ", allowed)}.");
        return null;
    }

    /// <summary>A JSON number in the range; a fraction is rounded to the nearest whole number.</summary>
    public int? Integer(string name, int min, int max, bool required)
    {
        var node = Get(name);
        if (node == null)
        {
            if (required) Fail($"'{name}' is required.");
            return null;
        }
        if (node is JsonValue v && v.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            var rounded = Math.Round(d, MidpointRounding.AwayFromZero);
            if (rounded >= min && rounded <= max) return (int)rounded;
        }
        Fail($"'{name}' must be a number from {min} to {max}.");
        return null;
    }

    /// <summary>A JSON array; null (no problem recorded) when absent or null and not required.</summary>
    public JsonArray? Array(string name, bool required)
    {
        var node = Get(name);
        if (node == null)
        {
            if (required) Fail($"'{name}' is required.");
            return null;
        }
        if (node is JsonArray a) return a;
        Fail($"'{name}' must be an array.");
        return null;
    }

    private JsonNode? Get(string name) =>
        Object.TryGetPropertyValue(name, out var node) && node != null ? node : null;
}
