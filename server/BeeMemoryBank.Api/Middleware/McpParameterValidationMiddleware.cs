using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Helpers;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Intercepts MCP <c>tools/call</c> JSON-RPC requests and rejects calls that pass
/// unknown parameter names. By default the SDK silently ignores them, which sends
/// weak models into guess-the-flag loops (e.g. trying 'include_content',
/// 'include_metadata' when the actual name is 'content'). On unknown names we
/// short-circuit with a JSON-RPC tool error that lists the full parameter schema
/// — the same information the model should have read up front.
/// </summary>
public class McpParameterValidationMiddleware(RequestDelegate next, McpToolRegistry registry, ILogger<McpParameterValidationMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private sealed record ToolParamAliasRule(
        IReadOnlyDictionary<string, string> Rename,
        IReadOnlySet<string> Drop);

    // Coding-agent file-edit tools (Claude Code, opencode, etc.) use old_string/new_string or
    // oldString/newString for "find and replace" — models reflexively reach for those names here
    // too. Silently accept them instead of forcing a wasted round-trip through the error message.
    private static readonly Dictionary<string, ToolParamAliasRule> AliasRules = new(StringComparer.Ordinal)
    {
        ["bee_replace_in_article"] = new ToolParamAliasRule(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["old_string"] = "search",
                ["oldString"] = "search",
                ["new_string"] = "replace",
                ["newString"] = "replace",
            },
            new HashSet<string>(StringComparer.Ordinal) { "filePath", "file_path" })
    };

    // Bounds how much of an oversized/wrong-shaped argument value (e.g. a whole article body
    // or base64 payload mistakenly passed as an 'id') gets echoed back into the error message
    // and the log -- both of which a model or operator will read in full.
    private static string Truncate(string s) => s.Length > 80 ? s[..80] + "...(truncated)" : s;

    public async Task InvokeAsync(HttpContext context)
    {
        // Only POSTs with JSON bodies carry tools/call requests. SSE GETs and other
        // verbs pass through untouched.
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !(context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }
        context.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            await next(context);
            return;
        }

        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            await next(context);
            return;
        }

        // Not `using (doc)`: that form disposes whichever JsonDocument `doc` held at block
        // entry, not whatever it holds at block exit -- and the alias-rewrite block below
        // reassigns `doc` to a freshly-parsed document. `finally` reads the current value.
        try
        {
            // Only validate single requests; batches are rare in MCP and we let the SDK
            // handle them as-is rather than partially short-circuit.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                await next(context);
                return;
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodEl) ||
                methodEl.GetString() != "tools/call" ||
                !root.TryGetProperty("params", out var paramsEl) ||
                paramsEl.ValueKind != JsonValueKind.Object)
            {
                await next(context);
                return;
            }

            if (!paramsEl.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                await next(context);
                return;
            }

            var toolName = nameEl.GetString()!;
            var tool = registry.Get(toolName);
            if (tool == null)
            {
                // Unknown tool — let SDK handle the standard "Method not found" path.
                await next(context);
                return;
            }

            var hasArgsProperty = paramsEl.TryGetProperty("arguments", out var argsEl);
            var argsIsObject = hasArgsProperty && argsEl.ValueKind == JsonValueKind.Object;
            var argsIsAbsentOrNull = !hasArgsProperty || argsEl.ValueKind == JsonValueKind.Null;

            if (!argsIsObject && !argsIsAbsentOrNull)
            {
                // 'arguments' present but not an object (e.g. array/string/number) -- a malformed
                // shape we don't specifically handle; let the SDK reject it in its own standard way.
                await next(context);
                return;
            }

            // Omitted/null 'arguments' still needs the missing-required-parameter check below (a
            // tool with any required parameter called with no arguments at all must not silently
            // reach the SDK's own opaque invocation failure) -- so this does NOT early-return the
            // way it used to; it just means there's nothing to alias-rewrite or enumerate names from.

            if (argsIsObject && AliasRules.TryGetValue(toolName, out var rule))
            {
                var needsRewrite = argsEl.EnumerateObject()
                    .Any(p => rule.Drop.Contains(p.Name) || rule.Rename.ContainsKey(p.Name));
                if (needsRewrite)
                {
                    var rootNode = JsonNode.Parse(body)!;
                    var argsNode = rootNode["params"]!["arguments"]!.AsObject();
                    foreach (var key in argsNode.Select(kv => kv.Key).ToList())
                    {
                        if (rule.Drop.Contains(key))
                        {
                            argsNode.Remove(key);
                        }
                        else if (rule.Rename.TryGetValue(key, out var canonical) && !argsNode.ContainsKey(canonical))
                        {
                            var value = argsNode[key];
                            argsNode.Remove(key);
                            argsNode[canonical] = value;
                        }
                    }

                    body = rootNode.ToJsonString();
                    var newBytes = Encoding.UTF8.GetBytes(body);
                    context.Request.Body = new MemoryStream(newBytes);
                    context.Request.ContentLength = newBytes.Length;

                    doc.Dispose();
                    doc = JsonDocument.Parse(body);
                    root = doc.RootElement;
                    root.TryGetProperty("params", out paramsEl);
                    paramsEl.TryGetProperty("arguments", out argsEl);

                    logger.LogInformation("MCP {Tool} rewrote aliased parameter name(s) in request", toolName);
                }
            }

            var validNames = new HashSet<string>(tool.Parameters.Select(p => p.Name), StringComparer.Ordinal);
            var providedNames = argsIsObject
                ? new HashSet<string>(argsEl.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var unknown = providedNames.Where(n => !validNames.Contains(n)).ToList();
            // Symmetric to the unknown-name check above: a required parameter can go missing
            // entirely (not just supplied under a wrong name) -- e.g. a coding-agent's file-edit
            // tool habit supplies search/replace-equivalent values but has no concept of 'id' at
            // all. Without this check, such a call sails past the check above (nothing
            // "unknown") and fails deep in the SDK's own parameter binder with an opaque "An
            // error occurred invoking {tool}" instead of a message naming the missing parameter.
            var missing = tool.Parameters
                .Where(p => p.Required && !providedNames.Contains(p.Name))
                .Select(p => p.Name)
                .ToList();

            // Guid-typed parameters (every article/media 'id') are bound directly to
            // System.Guid by the SDK's own JSON argument binder -- unlike our string-typed
            // params (e.g. timestamps), there's no method-body try/catch standing between a
            // malformed value and the SDK. A non-GUID string (a tree path is the common case:
            // agents that only know an article by its path in the tree, not its GUID) throws
            // deep inside the SDK's binder with the same opaque "An error occurred invoking
            // {tool}" message that missing/unknown params used to produce, so it needs the
            // same up-front check.
            var invalid = new List<string>();
            if (argsIsObject)
            {
                foreach (var param in tool.Parameters.Where(p => p.IsGuid))
                {
                    if (!argsEl.TryGetProperty(param.Name, out var valueEl))
                        continue; // absent entirely -- already covered by `missing` if required

                    if (valueEl.ValueKind == JsonValueKind.Null)
                    {
                        // Optional 'Guid?' params default to null -- a legitimate "no value".
                        // A required param is never 'Guid?' (it would have a default, making it
                        // optional), so an explicit null there can't bind to non-nullable Guid
                        // either -- treat it exactly like any other unparseable value.
                        if (param.Required)
                            invalid.Add($"'{param.Name}' must be a GUID, got null");
                        continue;
                    }

                    var raw = valueEl.ValueKind == JsonValueKind.String ? valueEl.GetString() : null;
                    if (raw == null || !Guid.TryParse(raw, out _))
                    {
                        var shown = valueEl.ValueKind == JsonValueKind.String ? Truncate($"\"{raw}\"") : Truncate(valueEl.GetRawText());
                        invalid.Add($"'{param.Name}' must be a GUID, got {shown}");
                    }
                }
            }

            if (unknown.Count == 0 && missing.Count == 0 && invalid.Count == 0)
            {
                await next(context);
                return;
            }

            logger.LogInformation(
                "MCP {Tool} called with unknown params: {Unknown}, missing required params: {Missing}, invalid values: {Invalid}",
                toolName, string.Join(", ", unknown), string.Join(", ", missing), string.Join(", ", invalid));

            var problems = new List<string>();
            if (unknown.Count > 0)
                problems.Add($"Unknown parameter(s): {string.Join(", ", unknown.Select(u => $"'{u}'"))}");
            if (missing.Count > 0)
                problems.Add($"Missing required parameter(s): {string.Join(", ", missing.Select(m => $"'{m}'"))}");
            if (invalid.Count > 0)
                problems.Add($"Invalid parameter value(s): {string.Join(", ", invalid)}");

            var paramSection = McpToolRegistry.FormatParameters(tool);
            var message =
                $"Error: {string.Join("; ", problems)} for {toolName}.\n\n" +
                $"Valid parameters for {toolName}:\n{paramSection}" +
                (invalid.Count > 0
                    ? "\n\nNote: GUID parameters take article/media IDs only, never tree paths. " +
                      "Resolve a path to its GUID first via bee_get_tree or bee_search, then pass that GUID here."
                    : "");

            object? requestId = root.TryGetProperty("id", out var idEl)
                ? JsonSerializer.Deserialize<JsonElement>(idEl.GetRawText())
                : null;

            var response = new
            {
                jsonrpc = "2.0",
                id = requestId,
                result = new
                {
                    isError = true,
                    content = new object[]
                    {
                        new { type = "text", text = message }
                    }
                }
            };

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOpts));
        }
        finally
        {
            doc.Dispose();
        }
    }
}
