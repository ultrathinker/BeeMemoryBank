using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// Keeps this test process out of the owner's profile (BMB-184, BMB-41). The node's data root, the default vault folder and
/// the ASP.NET data-protection key ring all sit under LOCALAPPDATA and APPDATA, and many tests build a web host or a node
/// without a data folder of their own. Every test in the process therefore gets LOCALAPPDATA and APPDATA of its own under
/// the temp folder, removed when the process exits. A test that sets them itself still does.
/// </summary>
internal static class TestDataIsolation
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmb-node-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "local"));
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(root, "roaming"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        };
    }
}
