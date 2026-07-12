using System.Diagnostics;
using System.Text.Json;
using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Node.Tests;

public class NodeOrchestratorTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly string _stubDllPath;

    public NodeOrchestratorTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "bmb-node-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);

        _stubDllPath = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Node.Tests.StubProcess.dll");
        if (!File.Exists(_stubDllPath))
        {
            throw new FileNotFoundException($"Stub process DLL not found at: {_stubDllPath}. Ensure the test project compiles and references it.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task DirectoryLock_Exclusive_ShouldPreventDoubleStartup()
    {
        // Arrange
        var configs = new List<ChildProcessConfig>(); // Empty is fine for lock test
        using var orchestrator1 = new NodeOrchestrator(_testDataDir, configs);
        using var orchestrator2 = new NodeOrchestrator(_testDataDir, configs);

        // Act & Assert
        // 1. First instance starts and locks the directory
        await orchestrator1.StartAsync(CancellationToken.None);
        orchestrator1.AllReady.Should().BeTrue();

        // 2. Second instance fails to start because lock is held
        var startFunc = () => orchestrator2.StartAsync(CancellationToken.None);
        await startFunc.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*lock*");

        // 3. Stop first instance to release lock
        await orchestrator1.StopAsync();

        // 4. Second instance can now start successfully
        await orchestrator2.StartAsync(CancellationToken.None);
        orchestrator2.AllReady.Should().BeTrue();

        await orchestrator2.StopAsync();
    }

    [Fact]
    public async Task Orchestrator_NormalStartupAndShutdown()
    {
        // Arrange
        var readyFile1 = Path.Combine(_testDataDir, "app1.ready");
        var readyFile2 = Path.Combine(_testDataDir, "app2.ready");

        var config1 = new ChildProcessConfig(
            ApplicationName: "App1",
            ExecutablePath: "dotnet",
            WorkingDirectory: AppContext.BaseDirectory,
            ReadyFilePath: readyFile1,
            Arguments: $"\"{_stubDllPath}\" --ready-file \"{readyFile1}\" --app-name App1 --urls http://127.0.0.1:9091"
        );

        var config2 = new ChildProcessConfig(
            ApplicationName: "App2",
            ExecutablePath: "dotnet",
            WorkingDirectory: AppContext.BaseDirectory,
            ReadyFilePath: readyFile2,
            Arguments: $"\"{_stubDllPath}\" --ready-file \"{readyFile2}\" --app-name App2 --urls http://127.0.0.1:9092"
        );

        using var orchestrator = new NodeOrchestrator(_testDataDir, new[] { config1, config2 });

        // Act & Assert
        // 1. Start and verify readiness
        await orchestrator.StartAsync(CancellationToken.None);
        orchestrator.AllReady.Should().BeTrue();

        // 2. Verify status file
        var statusPath = Path.Combine(_testDataDir, "node.status.json");
        File.Exists(statusPath).Should().BeTrue();

        var statusJson = File.ReadAllText(statusPath);
        var status = JsonSerializer.Deserialize<NodeStatus>(statusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        status.Should().NotBeNull();
        status!.Status.Should().Be("Ready");
        status.Children.Should().HaveCount(2);
        status.Children.Should().ContainKey("App1");
        status.Children.Should().ContainKey("App2");
        status.Children["App1"].Urls.Should().ContainSingle().Which.Should().Be("http://127.0.0.1:9091");

        // 3. Stop and verify files are cleaned up and processes are stopped
        await orchestrator.StopAsync();
        File.Exists(statusPath).Should().BeFalse();
        File.Exists(Path.Combine(_testDataDir, "node.lock")).Should().BeFalse();
    }

    [Fact]
    public async Task Orchestrator_ShouldRestartChild_OnUnexpectedExit()
    {
        // Arrange
        var readyFile = Path.Combine(_testDataDir, "restartapp.ready");
        var config = new ChildProcessConfig(
            ApplicationName: "RestartApp",
            ExecutablePath: "dotnet",
            WorkingDirectory: AppContext.BaseDirectory,
            ReadyFilePath: readyFile,
            Arguments: $"\"{_stubDllPath}\" --ready-file \"{readyFile}\" --app-name RestartApp --urls http://127.0.0.1:9093"
        );

        // Fast backoff for testing
        using var orchestrator = new NodeOrchestrator(_testDataDir, new[] { config }, _ => TimeSpan.FromMilliseconds(50));

        // Act & Assert
        await orchestrator.StartAsync(CancellationToken.None);
        orchestrator.AllReady.Should().BeTrue();

        // Read initial PID
        var readResult = ReadyFileManager.Read(readyFile);
        readResult.Success.Should().BeTrue();
        int initialPid = readResult.Info!.Pid;

        // Kill the child process
        using (var procToKill = Process.GetProcessById(initialPid))
        {
            procToKill.Kill(entireProcessTree: true);
        }

        // Wait for the orchestrator to detect exit, back off, and restart it.
        // We poll the ready file until the PID changes.
        var stopwatch = Stopwatch.StartNew();
        int newPid = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var currentResult = ReadyFileManager.Read(readyFile);
            if (currentResult.Success && currentResult.Info != null && currentResult.Info.Pid != initialPid)
            {
                newPid = currentResult.Info.Pid;
                break;
            }
            await Task.Delay(100);
        }

        newPid.Should().NotBe(0, "The child process should have been restarted with a new PID.");
        newPid.Should().NotBe(initialPid);

        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Orchestrator_ShouldFail_AfterFiveCrashes()
    {
        // Arrange
        var readyFile = Path.Combine(_testDataDir, "crashingapp.ready");
        var config = new ChildProcessConfig(
            ApplicationName: "CrashingApp",
            ExecutablePath: "dotnet",
            WorkingDirectory: AppContext.BaseDirectory,
            ReadyFilePath: readyFile,
            Arguments: $"\"{_stubDllPath}\" --crash-immediately"
        );

        // Fast backoff for testing (50ms per attempt)
        using var orchestrator = new NodeOrchestrator(_testDataDir, new[] { config }, _ => TimeSpan.FromMilliseconds(50));

        bool criticalFailureTriggered = false;
        orchestrator.OnCriticalFailure += _ => criticalFailureTriggered = true;

        // Act & Assert
        var startFunc = () => orchestrator.StartAsync(CancellationToken.None);
        await startFunc.Should().ThrowAsync<InvalidOperationException>();

        orchestrator.HasFailed.Should().BeTrue();
        criticalFailureTriggered.Should().BeTrue();

        await orchestrator.StopAsync();
    }
}
