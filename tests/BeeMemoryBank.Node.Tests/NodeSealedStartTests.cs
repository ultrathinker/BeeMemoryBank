using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// The desktop node's start over a folder that is encrypted or half-way to it (docs/design/step-b-spec.md 5.8, 10.6, red test 15.3 "Node.Tests"): the
/// orchestrator opens no database, passes the vault gate (an interrupted "Encrypt this node" that never verified is undone before anything starts), and
/// changes no file of a folder it cannot judge. The real node binary runs, with the stub process as its child.
/// </summary>
[Collection("NodeProcessEnv")]
public sealed class NodeSealedStartTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-node-sealed-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;
    private readonly string _stubDll = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Node.Tests.StubProcess.dll");

    public NodeSealedStartTests()
    {
        _d = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_d);
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

    private static byte[] Sqlite(int pages)
    {
        var bytes = new byte[4096 * pages];
        "SQLite format 3\0"u8.CopyTo(bytes);
        RandomNumberGenerator.Fill(bytes.AsSpan(100));
        return bytes;
    }

    private Dictionary<string, string> Hashes() => Directory.EnumerateFiles(_d, "*", SearchOption.AllDirectories)
        .Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))   // the node's own locks (node.lock, .instance.lock): held by it, holding no data
        .ToDictionary(f => Path.GetRelativePath(_d, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    [Fact]
    public async Task A_node_over_a_migration_that_never_verified_undoes_it_before_it_starts_anything()
    {
        File.WriteAllBytes(Path.Combine(_d, "beememorybank.db"), Sqlite(3));
        File.WriteAllBytes(Path.Combine(_d, "beememorybank.db.seal-new"), new byte[4096 * 3]);
        SealJournal.Write(_d, SealJournal.New(SealJournal.Building, ["beememorybank.db"], snapshot: ""));
        var live = File.ReadAllBytes(Path.Combine(_d, "beememorybank.db"));

        await using var node = Start(WriteConfig());

        (await WaitForAsync(() => File.Exists(ReadyFile))).Should().BeTrue(node.Output);
        File.Exists(Path.Combine(_d, "beememorybank.db.seal-new")).Should().BeFalse("the half-made target is deleted");
        File.Exists(SealJournal.PathFor(_d)).Should().BeFalse("the journal goes with it");
        File.ReadAllBytes(Path.Combine(_d, "beememorybank.db")).Should().Equal(live, "the live file was never touched");
    }

    [Fact]
    public async Task A_node_over_an_encrypted_folder_starts_its_children_and_opens_and_changes_no_file()
    {
        // An encrypted database has no SQLite header; the boot store is a plain SQLite file. The node judges nothing here: its Api does.
        var encrypted = new byte[4096 * 4];
        RandomNumberGenerator.Fill(encrypted);
        File.WriteAllBytes(Path.Combine(_d, "beememorybank.db"), encrypted);
        File.WriteAllBytes(Path.Combine(_d, "beememorybank.boot.db"), Sqlite(2));
        var before = Hashes();

        await using var node = Start(WriteConfig());

        (await WaitForAsync(() => File.Exists(ReadyFile))).Should().BeTrue(node.Output);
        // The files that were there are byte for byte what they were; the node adds only its own bookkeeping (the file the network front writes).
        var after = Hashes();
        after.Where(f => before.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => f.Value).Should().BeEquivalentTo(before, "the orchestrator opens no database and writes nothing into the folder");
        after.Keys.Except(before.Keys).Should().BeSubsetOf(["network.listening.json", ".runtime.json", "node.status.json"], "nothing but its own bookkeeping appears");
    }

    [Fact]
    public async Task A_node_over_leftovers_it_cannot_place_changes_nothing_and_leaves_the_refusal_to_its_api()
    {
        // A plain live database with a *.seal-old beside it and no journal: the resolver refuses (seal_leftovers) and deletes, moves and renames nothing.
        File.WriteAllBytes(Path.Combine(_d, "beememorybank.db"), Sqlite(3));
        File.WriteAllBytes(Path.Combine(_d, "beememorybank.db.seal-old"), Sqlite(3));
        var before = Hashes();

        await using var node = Start(WriteConfig());

        (await WaitForAsync(() => File.Exists(ReadyFile))).Should().BeTrue(node.Output);
        var after = Hashes();
        after.Where(f => before.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => f.Value).Should().BeEquivalentTo(before, "no file is deleted, moved, renamed or changed");
        after.Keys.Except(before.Keys).Should().BeSubsetOf(["network.listening.json", ".runtime.json", "node.status.json"], "nothing but its own bookkeeping appears");
    }
}
