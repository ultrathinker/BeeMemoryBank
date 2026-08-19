using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Reflection-built registry of MCP tool parameter schemas, used by the parameter
/// validation middleware. The MCP SDK silently ignores unknown parameters during
/// binding — which lets weak models loop indefinitely guessing names like
/// 'include_content'. The middleware queries this registry to catch unknown
/// parameter names before they reach the SDK.
/// </summary>
public class McpToolRegistry
{
    public record ParamInfo(string Name, string TypeLabel, bool Required, string? Default, string Description, bool IsGuid);

    public record ToolInfo(string Name, IReadOnlyList<ParamInfo> Parameters, bool RequiresUnlockedSession);

    private readonly Dictionary<string, ToolInfo> _tools;

    public McpToolRegistry(IEnumerable<Type> toolTypes)
    {
        _tools = new Dictionary<string, ToolInfo>(StringComparer.Ordinal);
        foreach (var t in toolTypes)
            RegisterType(t);
    }

    public ToolInfo? Get(string toolName) =>
        _tools.TryGetValue(toolName, out var info) ? info : null;

    private void RegisterType(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (toolAttr == null) continue;

            var name = !string.IsNullOrWhiteSpace(toolAttr.Name) ? toolAttr.Name! : method.Name;
            var requiresUnlocked = method.GetCustomAttribute<RequiresUnlockedSessionAttribute>() != null;

            var parameters = new List<ParamInfo>();
            foreach (var p in method.GetParameters())
            {
                // The SDK convention used across BeeMemoryBank: every tool argument
                // carries a [Description]. Services injected via DI do not.
                var desc = p.GetCustomAttribute<DescriptionAttribute>();
                if (desc == null) continue;

                var underlyingType = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;

                parameters.Add(new ParamInfo(
                    Name: p.Name ?? "?",
                    TypeLabel: FormatType(p.ParameterType),
                    Required: !p.HasDefaultValue,
                    Default: p.HasDefaultValue ? FormatDefault(p.DefaultValue) : null,
                    Description: desc.Description,
                    IsGuid: underlyingType == typeof(Guid)
                ));
            }

            _tools[name] = new ToolInfo(name, parameters, requiresUnlocked);
        }
    }

    private static string FormatType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        var label = u switch
        {
            _ when u == typeof(string) => "string",
            _ when u == typeof(bool) => "bool",
            _ when u == typeof(int) => "int",
            _ when u == typeof(long) => "int",
            _ when u == typeof(Guid) => "string (GUID)",
            _ when u == typeof(DateTime) => "string (ISO date)",
            _ when u.IsEnum => "string (enum)",
            _ => u.Name
        };
        return u != t ? label + "?" : label;
    }

    private static string FormatDefault(object? value)
    {
        if (value == null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is string s) return $"\"{s}\"";
        return value.ToString() ?? "null";
    }

    /// <summary>Formats parameter list as human-readable lines for error messages.</summary>
    public static string FormatParameters(ToolInfo tool)
    {
        if (tool.Parameters.Count == 0)
            return "  (this tool takes no parameters)";

        var lines = tool.Parameters.Select(p =>
        {
            var meta = p.Required ? "required" : $"optional, default {p.Default}";
            return $"  - {p.Name} ({p.TypeLabel}, {meta}) — {p.Description}";
        });
        return string.Join("\n", lines);
    }
}
