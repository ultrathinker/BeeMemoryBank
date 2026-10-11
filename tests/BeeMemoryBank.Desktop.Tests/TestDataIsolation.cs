using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// Keeps this test process out of the owner's profile (BMB-184, BMB-41). The node's data root and the default vault folder
/// sit under LOCALAPPDATA and APPDATA, and the node start-up checks the default vault folder even when a test gives it a
/// data folder of its own. Every test in the process therefore gets LOCALAPPDATA and APPDATA of its own under the temp
/// folder, removed when the process exits. A test that sets them itself still does.
/// </summary>
internal static class TestDataIsolation
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmb-desktop-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "local"));
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(root, "roaming"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        };
    }
}
