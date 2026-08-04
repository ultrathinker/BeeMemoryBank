namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Marks an MCP tool method that unconditionally needs to decrypt article/media content to do
/// anything useful. McpSessionGuardMiddleware checks this via McpToolRegistry before the tool
/// runs, so a locked session produces one consistent, clear error instead of relying on every
/// tool author to remember their own session.IsUnlocked check.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresUnlockedSessionAttribute : Attribute;
