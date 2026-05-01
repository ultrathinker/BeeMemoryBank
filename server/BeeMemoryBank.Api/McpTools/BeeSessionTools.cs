using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeSessionTools(McpResponseManager responseManager)
{
    [McpServerTool(Name = "bee_set_max_tokens")]
    [Description(
        "Set the maximum token limit for MCP responses. Default: 10,000.\n" +
        "If responses are truncated, use bee_continue to read the rest.\n" +
        "Minimum: 1000. Values above 20,000 may cause issues with smaller models.")]
    public string SetMaxTokens(
        [Description("New token limit per response. Minimum 1000.")] int maxTokens)
    {
        responseManager.SetMaxTokens(maxTokens);
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
        "Saved responses expire after 24 hours — after that, re-run the original tool call.")]
    public string Continue(
        [Description("The guid from the truncation warning (hex string, e.g. 'a1b2c3d4e5f6...'). Copy it exactly.")] string guid,
        [Description("The character offset from the truncation warning (integer). Copy it exactly.")] int offset)
    {
        return responseManager.Continue(guid, offset);
    }
}
