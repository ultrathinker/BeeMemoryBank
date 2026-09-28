using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// Review release-b R1-3 and R1-4: a node started from a node.config.json (the configured mode, which used to skip it)
/// passes the vault gate. It refuses while a re-key runs, and finishes an interrupted swap before it starts anything.
/// Once its children are up, the first start on a swapped-in vault is complete, and its journal and re-key lock go.
/// The real node binary runs, with the stub process as its child.
/// </summary>
[Collection("NodeProcessEnv")]
public sealed class NodeVaultGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-node-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;
    private readonly string _stubDll = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Node.Tests.StubProcess.dll");

    public NodeVaultGateTests()
    {
        _d = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string ReadyFile => Path.Combine(_root, "stub.ready");

    private string WriteConfig()
    {
        var config = new NodeConfig(_d, new List<ChildConfig>
        {
            new("BeeMemoryBank.Api", "dotnet", AppContext.BaseDirectory, ReadyFile,
                $"\"{_stubDll}\" --ready-file \"{ReadyFile}\" --app-name BeeMemoryBank.Api --urls http://127.0.0.1:0"),
        });
        var path = Path.Combine(_root, "node.config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    private sealed class RunningNode(Process process, ConcurrentQueue<string> output) : IAsyncDisposable
    {
        public Process Process => process;
        public string Output => string.Join("\n", output);

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(); // SIGKILL is asynchronous on Linux
                }
            }
            process.Dispose();
        }
    }

    private RunningNode Start(string configPath)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "BeeMemoryBank.Node.exe" : "BeeMemoryBank.Node");
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = $"\"{configPath}\"", WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.EnvironmentVariables["BMB_STDIN_LIFELINE"] = "1";
        var output = new ConcurrentQueue<string>();
        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new RunningNode(process, output);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds = 45)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    /// <summary>A swap stopped between its renames: D gone, the old vault aside, the verified new one waiting.</summary>
    private string StageInterruptedSwap()
    {
        var newDir = RekeySwapJournal.NewDirFor(_d);
        var oldDir = RekeySwapJournal.OldDirFor(_d, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "marker"), "new vault");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "marker"), "old vault");
        RekeySwapJournal.Write(_d, new RekeySwapJournal(newDir, oldDir, RekeySwapJournal.OldMoved));
        File.WriteAllText(RekeySwapJournal.LockPathFor(_d), ""); // the dead verb's lock file, held by nobody
        return oldDir;
    }

    [Fact]
    public async Task AConfiguredNode_RefusesToStart_WhileAReKeyRuns()
    {
        Directory.CreateDirectory(_d);
        using var running = RekeyLock.TryAcquire(_d)!;

        await using var node = Start(WriteConfig());

        (await WaitForAsync(() => node.Process.HasExited, 30)).Should().BeTrue(node.Output);
        node.Process.ExitCode.Should().Be(5, node.Output);
        File.Exists(ReadyFile).Should().BeFalse("no child was started");
    }

    [Fact]
    public async Task AConfiguredNode_FinishesAnInterruptedSwap_BeforeItStartsAnything()
    {
        var oldDir = StageInterruptedSwap();

        await using var node = Start(WriteConfig());

        (await WaitForAsync(() => File.Exists(ReadyFile))).Should().BeTrue(node.Output);
        File.ReadAllText(Path.Combine(_d, "marker")).Should().Be("new vault", "the swap was finished, not an empty D created");
        File.ReadAllText(Path.Combine(oldDir, "marker")).Should().Be("old vault");
    }

    /// <summary>
    /// R1-4: once the node's children are up, the first start on the swapped-in vault succeeded, and the node clears
    /// the journal and the re-key lock itself. The child here is the stub, not the real Api, so only the node can.
    /// </summary>
    [Fact]
    public async Task OnceItsChildrenAreUp_TheNodeClearsTheSwapJournalAndTheReKeyLock()
    {
        StageInterruptedSwap();

        await using var node = Start(WriteConfig());

        (await WaitForAsync(() => File.Exists(ReadyFile))).Should().BeTrue(node.Output);
        // Journal first, then the lock: wait for both (checking the lock the moment the journal vanishes races the node).
        (await WaitForAsync(() => !File.Exists(RekeySwapJournal.PathFor(_d)) && !File.Exists(RekeySwapJournal.LockPathFor(_d)), 20))
            .Should().BeTrue("the journal and the re-key lock go after the first successful start\n" + node.Output);
    }
}
