using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Completeness/parity guard for the two independent AI tool surfaces (MCP under
/// server/BeeMemoryBank.Api/McpTools/, native chat via ChatToolDispatcher). Both declare the SAME
/// tool names separately — MCP via [McpServerTool] + reflected C# parameters, chat via a
/// hand-built JSON-Schema in ChatToolDispatcher.ToolDefinitions — and nothing before this test
/// stopped the two declarations from drifting apart (different parameter names, different
/// required-ness, a parameter added to one side and not the other).
///
/// <para>Follows the completeness-checking style of McpToolRegistryTests: an explicit curated set
/// (<see cref="SharedToolMcpType"/>) names every tool this test knows to be shared, and a separate
/// test (<see cref="SharedToolSet_MatchesTheToolsActuallyPresentOnBothSurfaces"/>) asserts that set
/// is exactly the actual overlap — so a NEW tool exposed on both surfaces must be added here (and
/// diffed) rather than silently passing unchecked.</para>
/// </summary>
public class ChatMcpToolContractParityTests
{
    private enum ParamKind { String, Bool, Int, Guid, StringArray, Other }

    private sealed record NormalizedParam(string Name, ParamKind Kind, bool Required);

    // Every MCP tool name exposed to BOTH surfaces, and the MCP class it is declared on. This is
    // the curated list Step 3 requires: a tool newly exposed on both surfaces must be added here.
    private static readonly Dictionary<string, Type> SharedToolMcpType = new(StringComparer.Ordinal)
    {
        ["bee_search"] = typeof(BeeSearchTools),
        ["bee_list_articles"] = typeof(BeeReadTools),
        ["bee_get_tree"] = typeof(BeeReadTools),
        ["bee_get_article"] = typeof(BeeReadTools),
        ["bee_search_content"] = typeof(BeeSearchTools),
        ["bee_save_article"] = typeof(BeeWriteTools),
        ["bee_update_article"] = typeof(BeeWriteTools),
        ["bee_append_to_article"] = typeof(BeeWriteTools),
        ["bee_replace_in_article"] = typeof(BeeWriteTools),
        ["bee_delete_article"] = typeof(BeeWriteTools),
    };

    // MCP-only parameters that chat deliberately does NOT expose, curated explicitly so any OTHER
    // (undocumented) name/type/required-ness drift still fails ParameterContract_MatchesBetweenMcpAndChat
    // below. Both are scale-oriented pagination/delta params (bee_list_articles.updatedAfter lets an
    // agent fetch just what changed since its last call; bee_get_tree.depth/limit/offset page a huge
    // tree) added for large vaults -- chat's tool surface is deliberately smaller and has no use for
    // either. If one of these is ever added to chat too, remove it from here so the name/type/required
    // check below actually compares it.
    private static readonly Dictionary<string, string[]> KnownMcpOnlyExtraParams = new(StringComparer.Ordinal)
    {
        ["bee_list_articles"] = ["updatedAfter"],
        ["bee_get_tree"] = ["depth", "limit", "offset"],
    };

    public static IEnumerable<object[]> SharedToolNames() =>
        SharedToolMcpType.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(SharedToolNames))]
    public void ParameterContract_MatchesBetweenMcpAndChat(string toolName)
    {
        var mcpParams = GetMcpParams(SharedToolMcpType[toolName], toolName);
        var chatParams = GetChatParams(toolName);
        var allowedExtra = KnownMcpOnlyExtraParams.GetValueOrDefault(toolName, []);

        foreach (var extra in allowedExtra)
            mcpParams.Should().ContainKey(extra, $"{toolName} is documented here to carry an MCP-only '{extra}' param");

        var mcpCore = mcpParams
            .Where(kv => !allowedExtra.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        mcpCore.Keys.Should().BeEquivalentTo(chatParams.Keys,
            $"{toolName}'s parameter NAMES must match between MCP and chat (aside from the documented MCP-only extras) " +
            "-- if this fails, either a parameter was added to only one surface, or KnownMcpOnlyExtraParams needs updating");

        foreach (var (name, mcpParam) in mcpCore)
        {
            var chatParam = chatParams[name];
            chatParam.Kind.Should().Be(mcpParam.Kind, $"{toolName}.{name}'s TYPE must match between MCP and chat");
            chatParam.Required.Should().Be(mcpParam.Required, $"{toolName}.{name}'s REQUIRED-NESS must match between MCP and chat");
        }
    }

    [Fact]
    public void SharedToolSet_MatchesTheToolsActuallyPresentOnBothSurfaces()
    {
        var mcpNames = AllMcpToolNames();
        var chatNames = ChatToolDispatcher.ToolDefinitions.Select(d => d.Function.Name).ToHashSet(StringComparer.Ordinal);
        var actualOverlap = new HashSet<string>(mcpNames.Intersect(chatNames), StringComparer.Ordinal);

        actualOverlap.Should().BeEquivalentTo(SharedToolMcpType.Keys,
            "a tool newly exposed on BOTH surfaces (or one removed from one of them) must be added to " +
            "(or removed from) SharedToolMcpType in this file so its parameter contract gets diffed too");
    }

    // ── reflection helpers ──────────────────────────────────────────────────

    private static Dictionary<string, NormalizedParam> GetMcpParams(Type type, string mcpToolName)
    {
        var method = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == mcpToolName);

        var dict = new Dictionary<string, NormalizedParam>(StringComparer.Ordinal);
        foreach (var p in method.GetParameters())
        {
            // Mirrors McpToolRegistry's own convention: a parameter without [Description] is not
            // part of the tool's declared schema (DI-injected services carry none).
            if (p.GetCustomAttribute<DescriptionAttribute>() == null) continue;
            dict[p.Name!] = new NormalizedParam(p.Name!, NormalizeClrType(p.ParameterType), !p.HasDefaultValue);
        }
        return dict;
    }

    private static ParamKind NormalizeClrType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(Guid)) return ParamKind.Guid;
        if (u == typeof(string)) return ParamKind.String;
        if (u == typeof(bool)) return ParamKind.Bool;
        if (u == typeof(int) || u == typeof(long)) return ParamKind.Int;
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(u) && u != typeof(string)) return ParamKind.StringArray;
        return ParamKind.Other;
    }

    private static Dictionary<string, NormalizedParam> GetChatParams(string toolName)
    {
        var def = ChatToolDispatcher.ToolDefinitions.First(d => d.Function.Name == toolName);
        var schema = def.Function.Parameters;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
            foreach (var r in reqEl.EnumerateArray())
                required.Add(r.GetString()!);

        var dict = new Dictionary<string, NormalizedParam>(StringComparer.Ordinal);
        foreach (var prop in schema.GetProperty("properties").EnumerateObject())
        {
            var jsonType = prop.Value.GetProperty("type").GetString();
            var kind = jsonType switch
            {
                "string" => prop.Value.TryGetProperty("format", out var f) && f.GetString() == "uuid"
                    ? ParamKind.Guid
                    : ParamKind.String,
                "boolean" => ParamKind.Bool,
                "integer" => ParamKind.Int,
                // Every array param declared in ChatToolDispatcher.ToolDefinitions is array-of-string
                // (tags) -- see BuildToolDefinitions' P() helper, which only ever sets itemsType:"string".
                "array" => ParamKind.StringArray,
                _ => ParamKind.Other
            };
            dict[prop.Name] = new NormalizedParam(prop.Name, kind, required.Contains(prop.Name));
        }
        return dict;
    }

    /// <summary>Every MCP tool name in the assembly, found the same way McpToolRegistryTests does
    /// (a full [McpServerTool] scan, not a hardcoded class list) so a new tool CLASS cannot be
    /// invisible to this test.</summary>
    private static HashSet<string> AllMcpToolNames()
    {
        var assembly = typeof(BeeSearchTools).Assembly;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                names.Add(string.IsNullOrWhiteSpace(attr.Name) ? method.Name : attr.Name!);
            }
        }
        names.Should().NotBeEmpty("the [McpServerTool] scan must actually find the MCP tools");
        return names;
    }
}

/// <summary>
/// Pure-logic tests for <see cref="ChatToolDispatcher.RequiresUnlockedSessionForCall"/> — the
/// predicate that replaced ChatToolDispatcher's old blanket "every write tool needs an unlocked
/// session" gate. That blanket gate over-blocked two calls MCP's own
/// [RequiresUnlockedSession] classification (McpToolRegistryTests' DeliberatelyUnguarded set)
/// deliberately allows while locked: a metadata-only bee_update_article (no content -- never
/// touches the encrypted body) and any bee_delete_article (a soft-delete only flips a status
/// column). No database needed -- this only exercises the classification logic itself.
/// </summary>
public class ChatWriteToolLockGateUnitTests
{
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

    private static JsonElement Args(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    [Fact]
    public void UpdateArticle_WithContent_RequiresUnlockedSession()
    {
        ChatToolDispatcher.RequiresUnlockedSessionForCall(
            "bee_update_article", Args(new { id = Guid.NewGuid().ToString(), content = "new body" }), Registry)
            .Should().BeTrue();
    }

    [Fact]
    public void UpdateArticle_TitleOnly_DoesNotRequireUnlockedSession()
    {
        ChatToolDispatcher.RequiresUnlockedSessionForCall(
            "bee_update_article", Args(new { id = Guid.NewGuid().ToString(), title = "New Title" }), Registry)
            .Should().BeFalse();
    }

    [Fact]
    public void UpdateArticle_TagsOnly_DoesNotRequireUnlockedSession()
    {
        ChatToolDispatcher.RequiresUnlockedSessionForCall(
            "bee_update_article", Args(new { id = Guid.NewGuid().ToString(), tags = new[] { "a" } }), Registry)
            .Should().BeFalse();
    }

    [Fact]
    public void UpdateArticle_NoArgsAtAll_DoesNotRequireUnlockedSession()
    {
        ChatToolDispatcher.RequiresUnlockedSessionForCall("bee_update_article", default, Registry)
            .Should().BeFalse();
    }

    [Fact]
    public void DeleteArticle_NeverRequiresUnlockedSession()
    {
        ChatToolDispatcher.RequiresUnlockedSessionForCall(
            "bee_delete_article", Args(new { id = Guid.NewGuid().ToString(), confirm = true }), Registry)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("bee_save_article")]
    [InlineData("bee_append_to_article")]
    [InlineData("bee_replace_in_article")]
    public void UnconditionalWriteTools_AlwaysRequireUnlockedSession_MatchingMcpClassification(string toolName)
    {
        // These three read straight from McpToolRegistry's own [RequiresUnlockedSession]
        // classification -- pin that classification is actually True for all of them, so this
        // test fails loudly if a future edit ever removes the attribute from one on the MCP side
        // without anyone noticing the chat gate silently followed it down.
        Registry.Get(toolName)!.RequiresUnlockedSession.Should().BeTrue();

        ChatToolDispatcher.RequiresUnlockedSessionForCall(toolName, default, Registry)
            .Should().BeTrue();
    }

    [Fact]
    public void UnknownTool_DefaultsToRequiringUnlockedSession()
    {
        // bee_insert_image_into_article has no MCP counterpart at all (chat-only), so it falls
        // through to the fail-safe default -- it always touches the encrypted body, so the
        // fail-safe must be "true", not "false".
        ChatToolDispatcher.RequiresUnlockedSessionForCall("bee_insert_image_into_article", default, Registry)
            .Should().BeTrue();
    }
}
