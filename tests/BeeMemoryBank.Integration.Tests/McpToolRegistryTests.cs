using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.McpTools;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Verifies McpToolRegistry correctly classifies which MCP tools are marked
/// [RequiresUnlockedSession] — the classification McpSessionGuardMiddleware relies on to block
/// calls to unconditionally-content-touching tools while the session is locked.
/// </summary>
public class McpToolRegistryTests
{
    // Mirrors the tool-type list registered in Program.cs for the real McpToolRegistry singleton.
    private static readonly McpToolRegistry Registry = new(new[]
    {
        typeof(BeeSearchTools),
        typeof(BeeReadTools),
        typeof(BeeWriteTools),
        typeof(BeeSessionTools),
        typeof(BeeUploadTools),
        typeof(BeeAuditTools),
        typeof(BeeConceptTools)
    });

    [Theory]
    [InlineData("bee_get_article_version")]
    [InlineData("bee_get_article_diff")]
    [InlineData("bee_get_image")]
    [InlineData("bee_save_media")]
    [InlineData("bee_save_article")]
    [InlineData("bee_replace_in_article")]
    [InlineData("bee_append_to_article")]
    [InlineData("bee_prepend_to_article")]
    public void RequiresUnlockedSession_TrueForMarkedTools(string toolName)
    {
        var tool = Registry.Get(toolName);
        tool.Should().NotBeNull();
        tool!.RequiresUnlockedSession.Should().BeTrue();
    }

    [Theory]
    [InlineData("bee_list_articles")]
    [InlineData("bee_get_article")]
    [InlineData("bee_get_tree")]
    [InlineData("bee_get_article_versions")]
    [InlineData("bee_update_article")]
    [InlineData("bee_copy_to")]
    [InlineData("bee_search")]
    [InlineData("bee_search_content")]
    public void RequiresUnlockedSession_FalseForUnmarkedTools(string toolName)
    {
        var tool = Registry.Get(toolName);
        tool.Should().NotBeNull();
        tool!.RequiresUnlockedSession.Should().BeFalse();
    }
}
