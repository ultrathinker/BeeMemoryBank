using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Calls an MCP tool by its wire name on a tool-class instance, the way a client reaches it. A test
/// written with this fails on "no such tool" when the tool is missing, not on a compile error.
/// </summary>
internal static class McpToolInvoker
{
    public static async Task<IReadOnlyList<ContentBlock>> CallAsync(
        object tools, string toolName, params (string Name, object? Value)[] args)
    {
        var method = tools.GetType().GetMethods()
            .SingleOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        method.Should().NotBeNull($"{toolName} must be an MCP tool of {tools.GetType().Name}");

        var values = method!.GetParameters()
            .Select(p => args.Any(a => a.Name == p.Name) ? args.First(a => a.Name == p.Name).Value : p.DefaultValue)
            .ToArray();
        var task = (Task)method.Invoke(tools, values)!;
        await task;
        var blocks = (IEnumerable<ContentBlock>)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return blocks.ToList();
    }
}
