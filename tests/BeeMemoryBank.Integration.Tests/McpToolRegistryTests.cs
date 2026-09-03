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

    // ─── Every tool must be classified on purpose ──────────────────────────────
    //
    // The two [Theory] lists above assert the classification of the tools someone remembered to
    // list. A NEW tool is in neither, so it passes both — which is exactly the failure mode
    // AGENTS.md warns about: an MCP tool that touches encrypted content without
    // [RequiresUnlockedSession] does not error while the vault is locked, it returns EMPTY
    // RESULTS. An agent reads that as "the vault has nothing", not "the vault is locked".
    //
    // So the real guard is completeness: every registered tool name must appear in one of the two
    // curated sets below. Adding a tool then fails this test until someone decides, in writing,
    // which side it belongs on.

    private static readonly HashSet<string> MustHaveUnlockedSession = new(StringComparer.Ordinal)
    {
        // Unconditionally read or write encrypted content, and cannot do anything useful without
        // the DEK — the session guard turns "empty results" into an explicit "vault is locked".
        "bee_get_article_version",
        "bee_get_article_diff",
        "bee_get_image",
        "bee_save_media",
        "bee_save_article",
        "bee_replace_in_article",
        "bee_append_to_article",
        "bee_prepend_to_article",
    };

    private static readonly HashSet<string> DeliberatelyUnguarded = new(StringComparer.Ordinal)
    {
        // Metadata-only, or they handle the locked case themselves and report it. Titles, paths,
        // tags and timestamps are stored in plaintext by design (ADR-0005), so these stay useful
        // — and honest — with the vault locked.
        "bee_list_articles",
        "bee_get_article",
        "bee_get_tree",
        "bee_get_article_versions",
        "bee_update_article",
        "bee_copy_to",
        "bee_search",
        "bee_search_content",
        "bee_search_by_tag",
        "bee_get_related",
        "bee_get_log",
        "bee_get_upload_script",
        "bee_set_max_tokens",
        "bee_continue",
        "bee_delete_article",
        "bee_delete_folder",
        "bee_rename_folder",
        "bee_move_folder",
        "bee_add_tags",
        "bee_remove_tag",
        "bee_delete_tag",
        "bee_rename_tag",
        "bee_merge_tags",
        "bee_list_tags",
    };

    [Fact]
    public void EveryRegisteredTool_IsExplicitlyClassified()
    {
        var registered = AllToolNames();

        var unclassified = registered
            .Where(n => !MustHaveUnlockedSession.Contains(n) && !DeliberatelyUnguarded.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "a new MCP tool must be added to MustHaveUnlockedSession or DeliberatelyUnguarded in " +
            "this file. Decide which: a tool that reads or writes encrypted content and is NOT " +
            "marked [RequiresUnlockedSession] returns empty results while the vault is locked " +
            "instead of saying so, and an agent cannot tell the difference from an empty vault");

        // The reverse direction too: a name removed or renamed in production should not leave a
        // stale entry here quietly asserting something about a tool that no longer exists.
        var stale = MustHaveUnlockedSession.Concat(DeliberatelyUnguarded)
            .Where(n => !registered.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        stale.Should().BeEmpty("these names are classified here but no longer registered as tools");
    }

    [Fact]
    public void ToolsListedAsRequiringAnUnlockedSession_ActuallyCarryTheAttribute()
    {
        foreach (var name in MustHaveUnlockedSession)
            Registry.Get(name)!.RequiresUnlockedSession.Should().BeTrue(
                "{0} is classified as content-touching in this file", name);

        foreach (var name in DeliberatelyUnguarded)
            Registry.Get(name)!.RequiresUnlockedSession.Should().BeFalse(
                "{0} is classified as safe-while-locked in this file — if it grew a dependency on " +
                "decrypted content, move it to the other set rather than relaxing this", name);
    }

    /// <summary>
    /// Enumerates tool names the same way <see cref="McpToolRegistry"/> does — by reflecting over
    /// the same tool types for [McpServerTool] — rather than through a registry accessor, so the
    /// completeness check does not depend on production code growing an enumeration API.
    /// </summary>
    private static HashSet<string> AllToolNames()
    {
        var types = new[]
        {
            typeof(BeeSearchTools), typeof(BeeReadTools), typeof(BeeWriteTools),
            typeof(BeeSessionTools), typeof(BeeUploadTools), typeof(BeeAuditTools),
            typeof(BeeConceptTools)
        };

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var attr = method.GetCustomAttributes(inherit: false)
                    .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (attr == null) continue;

                var name = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        return names;
    }
}
