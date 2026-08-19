using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeSessionTools(McpResponseManager responseManager)
{
    [McpServerTool(Name = "bee_set_max_tokens")]
    [Description(
        "Set your own default maximum token limit for MCP responses. Default: 10,000. " +
        "Range: 1,000-100,000 (inclusive) — a value outside this range is rejected with an error " +
        "and the limit is left unchanged; it is never silently clamped. This only affects your " +
        "own future calls, not other agents' — but it also stays raised for all of YOUR future " +
        "calls, so prefer bee_continue's ignoreLimit parameter for a one-off large fetch instead.\n" +
        "If a response is truncated anyway, use bee_continue to read the rest.")]
    public string SetMaxTokens(
        [Description("New default token limit per response. Must be 1,000-100,000; out-of-range values return an error instead of being clamped.")] int maxTokens)
    {
        if (!responseManager.TrySetMaxTokens(maxTokens, out var error))
            return $"Error: {error}";
        return $"Max tokens set to {responseManager.MaxTokens}.";
    }

    [McpServerTool(Name = "bee_continue")]
    [Description(
        "Continue reading a response that was truncated due to the max_tokens limit.\n" +
        "How to recognise a truncated response — two possible formats:\n" +
        "  1) Plain text ends with: \"⚠️ TRUNCATED: ... Call bee_continue(guid: \\\"<hex>\\\", offset: <number>) ...\"\n" +
        "  2) JSON response with fields { truncated: true, guid: \"<hex>\", offset: <number>, hint: \"...\" }\n" +
        "Extract guid and offset from whichever format you got, then call this tool. Repeat until the chunk " +
        "returned has no truncation marker (or until you get status=\"complete\").\n" +
        "For getting everything in one call instead of paging through it: pass ignoreLimit: true. It bypasses " +
        "your current max-tokens limit for THIS call only (capped at a hard 100,000-token ceiling that no " +
        "call can exceed), without changing what your other calls return. The truncation hint always states " +
        "the exact remaining token count, so you can decide up front whether your own context can take it.\n" +
        "Saved responses expire after 24 hours — after that, re-run the original tool call.")]
    public string Continue(
        [Description("The guid from the truncation warning (hex string, e.g. 'a1b2c3d4e5f6...'). Copy it exactly.")] string guid,
        [Description("The character offset from the truncation warning (integer). Copy it exactly.")] int offset,
        [Description("If true, ignore your max-tokens limit for this call and return all remaining content up to the hard 100,000-token ceiling, instead of just the next chunk. Default: false.")] bool ignoreLimit = false)
    {
        return responseManager.Continue(guid, offset, ignoreLimit);
    }
}
