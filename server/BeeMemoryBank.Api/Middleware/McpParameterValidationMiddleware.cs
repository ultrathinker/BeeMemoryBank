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

            if (!paramsEl.TryGetProperty("arguments", out var argsEl) || argsEl.ValueKind != JsonValueKind.Object)
            {
                await next(context);
                return;
            }

            if (AliasRules.TryGetValue(toolName, out var rule))
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
            var unknown = new List<string>();
            foreach (var prop in argsEl.EnumerateObject())
            {
                if (!validNames.Contains(prop.Name))
                    unknown.Add(prop.Name);
            }

            if (unknown.Count == 0)
            {
                await next(context);
                return;
            }

            logger.LogInformation("MCP {Tool} called with unknown params: {Unknown}", toolName, string.Join(", ", unknown));

            var unknownList = string.Join(", ", unknown.Select(u => $"'{u}'"));
            var paramSection = McpToolRegistry.FormatParameters(tool);
            var message =
                $"Error: Unknown parameter(s) for {toolName}: {unknownList}.\n\n" +
                $"Valid parameters for {toolName}:\n{paramSection}";

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
