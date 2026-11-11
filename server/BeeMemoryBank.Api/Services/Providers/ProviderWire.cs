using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>The model answered, and its answer is a refusal (Anthropic's <c>refusal</c> stop reason) or was blocked by the provider's safety
/// filter (Gemini's <c>SAFETY</c>, <c>PROHIBITED_CONTENT</c> ... or a blocked prompt). Not a key's fault and nothing another key would change:
/// the chat shows the message and does not retry. An <see cref="InvalidOperationException"/>, so every chat path that reports a 200 that
/// is not a usable completion reports this one the same way.</summary>
public sealed class ProviderRefusalException(string message) : InvalidOperationException(message);

/// <summary>What the protocol clients share: the reading of a server-sent event stream, data URLs, and what of a failed request may be
/// logged - the short error code of a provider's error body and its request id, never the body or its message, which can quote what was
/// sent (decrypted notes, tool results, write arguments, thinking).</summary>
internal static partial class ProviderWire
{
    /// <summary>The output limit when the caller gives none and the protocol needs one (Anthropic's <c>max_tokens</c> is required). The
    /// chat sends no limit of its own; this is the cap of one chat round, the reasoning of a thinking model included.</summary>
    public const int DefaultMaxOutputTokens = 16000;

    /// <summary>The <c>data</c> of each event of a server-sent event stream, in order (several <c>data:</c> lines of one event joined by a line
    /// break, as the format says; comments, <c>event:</c> and <c>id:</c> lines skipped). Ends when the stream ends.</summary>
    public static async IAsyncEnumerable<string> ReadEventDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var any = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (any) yield return data.ToString();
                data.Clear();
                any = false;
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (any) data.Append('\n');
            data.Append(line.AsSpan("data:".Length).TrimStart(' '));
            any = true;
        }
        if (any) yield return data.ToString();
    }

    /// <summary>Splits <c>data:image/png;base64,....</c> into its media type and base64 payload.</summary>
    public static bool TryParseDataUrl(string? url, out string mediaType, out string base64)
    {
        mediaType = "";
        base64 = "";
        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var comma = url.IndexOf(',');
        if (comma < 0) return false;
        var header = url[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;
        mediaType = header[..^";base64".Length].Trim().ToLowerInvariant();
        base64 = url[(comma + 1)..];
        return mediaType.Length > 0 && base64.Length > 0;
    }

    [GeneratedRegex("^[A-Za-z_]{1,64}$")]
    private static partial Regex ErrorCodeShape();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,128}$")]
    private static partial Regex RequestIdShape();

    /// <summary>The provider's id of this request (<c>request-id</c>, <c>x-request-id</c>) when it is a plain token, for the log: it lets the
    /// provider's support find the request without the log holding anything of what was sent or answered.</summary>
    public static string? RequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "request-id", "x-request-id" })
            if (response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { } id && RequestIdShape().IsMatch(id))
                return id;
        return null;
    }

    /// <summary>The machine-readable code of an error body - Anthropic's <c>error.type</c> (<c>overloaded_error</c>), Google's
    /// <c>error.status</c> (<c>RESOURCE_EXHAUSTED</c>) - when it is one short word; never the message.</summary>
    public static string? ErrorCode(string? body, string field)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ErrorCode(doc.RootElement, field);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><see cref="ErrorCode(string?, string)"/> of an already parsed body (<c>{"error": {field: ...}}</c>).</summary>
    public static string? ErrorCode(JsonElement root, string field) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty(field, out var code) && code.ValueKind == JsonValueKind.String
        && code.GetString() is { } text && ErrorCodeShape().IsMatch(text)
            ? text
            : null;

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>A tool call's argument string as a JSON object, or an empty object when it is not one (a protocol that takes the arguments
    /// as an object cannot carry anything else).</summary>
    public static System.Text.Json.Nodes.JsonObject ArgumentsObject(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new System.Text.Json.Nodes.JsonObject();
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(argumentsJson) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        }
        catch (JsonException)
        {
            return new System.Text.Json.Nodes.JsonObject();
        }
    }
}
