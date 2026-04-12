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
        "Continue reading a truncated response.\n" +
        "When a response is truncated, you get a guid and offset. Pass them here for the next chunk.\n" +
        "Saved responses expire after 24 hours.")]
    public string Continue(
        [Description("The guid from the truncation warning.")] string guid,
        [Description("The character offset from the truncation warning.")] int offset)
    {
        return responseManager.Continue(guid, offset);
    }
}
