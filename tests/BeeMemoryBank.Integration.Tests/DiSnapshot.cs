using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Baselines for the vault split (BMB-99): a text snapshot of what a host registers in its container and serves as routes.
/// Types are written without their assembly (namespace + name), so moving a type to another assembly does not change a line;
/// adding, removing, re-lifetiming or re-ordering a registration does, and the diff of the golden file is then reviewed.
/// </summary>
internal static class DiSnapshot
{
    /// <summary>A type as namespace.Name (nested with '+', generics with &lt;args&gt;), without assembly names or versions.</summary>
    public static string Fmt(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return Fmt(t.GetElementType()!) + "[]";
        var name = t.DeclaringType is { } d ? Fmt(d) + "+" + Strip(t.Name) : (t.Namespace is { Length: > 0 } ns ? ns + "." : "") + Strip(t.Name);
        if (!t.IsGenericType) return name;
        var args = t.IsGenericTypeDefinition ? t.GetGenericArguments().Select(a => a.Name) : t.GetGenericArguments().Select(Fmt);
        return name + "<" + string.Join(",", args) + ">";
    }

    private static string Strip(string n) => n.Contains('`') ? n[..n.IndexOf('`')] : n;

    private static bool Ours(Type? t) => t?.Namespace is { } ns && ns.StartsWith("BeeMemoryBank", StringComparison.Ordinal);

    /// <summary>
    /// One line per registration whose service or implementation is an application type, or that is a hosted service:
    /// "Lifetime Service => Implementation". Sorted by service, registration order kept inside a service (the last one wins).
    /// </summary>
    public static string Container(IEnumerable<ServiceDescriptor> descriptors)
    {
        var rows = descriptors.Select((d, i) => (d, i)).Where(x =>
        {
            var impl = x.d.IsKeyedService ? x.d.KeyedImplementationType : x.d.ImplementationType;
            var inst = x.d.IsKeyedService ? x.d.KeyedImplementationInstance : x.d.ImplementationInstance;
            return Ours(x.d.ServiceType) || Ours(impl) || Ours(inst?.GetType()) || x.d.ServiceType.Name == "IHostedService";
        }).Select(x =>
        {
            var d = x.d;
            var impl = d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;
            var inst = d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance;
            var factory = d.IsKeyedService ? d.KeyedImplementationFactory is not null : d.ImplementationFactory is not null;
            var what = impl is not null ? Fmt(impl) : inst is not null ? "instance " + Fmt(inst.GetType()) : factory ? "factory" : "?";
            var key = d.IsKeyedService ? " [key " + d.ServiceKey + "]" : "";
            return (Service: Fmt(d.ServiceType), x.i, Line: $"{d.Lifetime} {Fmt(d.ServiceType)}{key} => {what}");
        }).OrderBy(r => r.Service, StringComparer.Ordinal).ThenBy(r => r.i).Select(r => r.Line);
        return string.Join("\n", rows) + "\n";
    }

    /// <summary>The hosted services the host really runs (factory registrations hide their type in the container list).</summary>
    public static string Hosted(IEnumerable<Microsoft.Extensions.Hosting.IHostedService> services) =>
        "# hosted services at run time\n" + string.Join("\n", services.Select(s => Fmt(s.GetType())).OrderBy(n => n, StringComparer.Ordinal)) + "\n";

    /// <summary>One line per route: "METHODS pattern | metadata type names" (compiler-generated names left out).</summary>
    public static string Routes(EndpointDataSource source)
    {
        var rows = source.Endpoints.OfType<RouteEndpoint>().Select(e =>
        {
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            var meta = e.Metadata.Select(m => m.GetType()).Where(t => !t.Name.Contains('<') && !t.Name.Contains('>'))
                .Select(Fmt).Distinct().OrderBy(n => n, StringComparer.Ordinal);
            return $"{string.Join(",", methods.OrderBy(m => m, StringComparer.Ordinal))} {e.RoutePattern.RawText} | {string.Join(" ", meta)}";
        }).Distinct().OrderBy(r => r, StringComparer.Ordinal);
        return string.Join("\n", rows) + "\n";
    }

    /// <summary>The repository root (the folder with BeeMemoryBank.slnx).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("the tests run from inside the repository");
        return dir.FullName;
    }

    /// <summary>Compares <paramref name="actual"/> with the golden file, or rewrites it when BMB_UPDATE_GOLDEN=1.</summary>
    public static string? Check(string relativePath, string actual)
    {
        var path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Environment.GetEnvironmentVariable("BMB_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return null;
        }
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        return actual == expected ? null : "differs from " + relativePath + " (regenerate with BMB_UPDATE_GOLDEN=1 and review the diff)";
    }
}
