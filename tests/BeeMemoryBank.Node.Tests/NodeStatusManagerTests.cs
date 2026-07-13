using System.Text.Json;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

public class NodeStatusManagerTests : IDisposable
{
    private readonly string _testDataDir;

    public NodeStatusManagerTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "bmb-node-status-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);
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
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void WriteStatus_WritesBothStatusAndRuntimeFiles_WithCorrectDefaults()
    {
        // Arrange
        var manager = new NodeStatusManager(_testDataDir);
        var children = new Dictionary<string, ReadyFileInfo>();

        var statusPath = Path.Combine(_testDataDir, "node.status.json");
        var runtimePath = Path.Combine(_testDataDir, ".runtime.json");

        // Act
        manager.WriteStatus(children);

        // Assert
        File.Exists(statusPath).Should().BeTrue();
        File.Exists(runtimePath).Should().BeTrue();

        var runtimeJson = File.ReadAllText(runtimePath);
        var runtime = JsonSerializer.Deserialize<RuntimeDescriptor>(runtimeJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        runtime.Should().NotBeNull();
        runtime!.Pid.Should().Be(Environment.ProcessId);
        runtime.FrontUrl.Should().BeNull();
        runtime.Version.Should().Be("1.0.1");
        runtime.Mode.Should().Be("production");
    }

    [Fact]
    public void WriteStatus_WritesBothStatusAndRuntimeFiles_WithCustomValues()
    {
        // Arrange
        const string customFrontUrl = "http://127.0.0.1:8080";
        const string customVersion = "2.3.4";
        const string customMode = "development";

        var manager = new NodeStatusManager(_testDataDir, customFrontUrl, customVersion, customMode);
        var children = new Dictionary<string, ReadyFileInfo>();

        var statusPath = Path.Combine(_testDataDir, "node.status.json");
        var runtimePath = Path.Combine(_testDataDir, ".runtime.json");

        // Act
        manager.WriteStatus(children);

        // Assert
        File.Exists(statusPath).Should().BeTrue();
        File.Exists(runtimePath).Should().BeTrue();

        var runtimeJson = File.ReadAllText(runtimePath);
        var runtime = JsonSerializer.Deserialize<RuntimeDescriptor>(runtimeJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        runtime.Should().NotBeNull();
        runtime!.Pid.Should().Be(Environment.ProcessId);
        runtime.FrontUrl.Should().Be(customFrontUrl);
        runtime.Version.Should().Be(customVersion);
        runtime.Mode.Should().Be(customMode);
    }

    [Fact]
    public void DeleteStatus_DeletesBothStatusAndRuntimeFiles()
    {
        // Arrange
        var manager = new NodeStatusManager(_testDataDir);
        var children = new Dictionary<string, ReadyFileInfo>();

        var statusPath = Path.Combine(_testDataDir, "node.status.json");
        var runtimePath = Path.Combine(_testDataDir, ".runtime.json");

        manager.WriteStatus(children);

        File.Exists(statusPath).Should().BeTrue();
        File.Exists(runtimePath).Should().BeTrue();

        // Act
        manager.DeleteStatus();

        // Assert
        File.Exists(statusPath).Should().BeFalse();
        File.Exists(runtimePath).Should().BeFalse();
    }

    // ── Этап 6, §6 пункт 1.2 — per-vault isolation ─────────────────────────────

    /// <summary>
    /// Two <see cref="NodeStatusManager"/> instances constructed with DIFFERENT
    /// <c>dataDirectory</c> values (mirroring two profiles/vaults) must each write their
    /// <c>node.status.json</c> / <c>.runtime.json</c> STRICTLY into their own directory. A
    /// status write for vault A must never appear under vault B's directory and vice versa.
    /// </summary>
    [Fact]
    public void TwoManagers_WithDifferentDataDirs_WriteStrictlyToTheirOwnDirectory()
    {
        // Arrange — two sibling vault directories under the test root.
        var dirA = Path.Combine(_testDataDir, "vaultA");
        var dirB = Path.Combine(_testDataDir, "vaultB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var statusFile = "node.status.json";
        var runtimeFile = ".runtime.json";

        // Distinct values per manager so a cross-contamination would be detectable by content,
        // not just by file presence.
        var mgrA = new NodeStatusManager(dirA, frontUrl: "http://127.0.0.1:7001", version: "1.2.3", mode: "prod");
        var mgrB = new NodeStatusManager(dirB, frontUrl: "http://127.0.0.1:7002", version: "9.8.7", mode: "dev");

        var emptyChildren = new Dictionary<string, ReadyFileInfo>();

        // Act
        mgrA.WriteStatus(emptyChildren);
        mgrB.WriteStatus(emptyChildren);

        // Assert — each directory contains exactly one of each file.
        Directory.GetFiles(dirA, statusFile).Should().HaveCount(1);
        Directory.GetFiles(dirA, runtimeFile).Should().HaveCount(1);
        Directory.GetFiles(dirB, statusFile).Should().HaveCount(1);
        Directory.GetFiles(dirB, runtimeFile).Should().HaveCount(1);

        // The runtime descriptors must carry their OWN manager's values — proving the file in
        // dirA was written by mgrA and not overwritten or shadowed by mgrB.
        var runtimeA = JsonSerializer.Deserialize<RuntimeDescriptor>(
            File.ReadAllText(Path.Combine(dirA, runtimeFile)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var runtimeB = JsonSerializer.Deserialize<RuntimeDescriptor>(
            File.ReadAllText(Path.Combine(dirB, runtimeFile)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        runtimeA!.FrontUrl.Should().Be("http://127.0.0.1:7001");
        runtimeA.Version.Should().Be("1.2.3");
        runtimeB!.FrontUrl.Should().Be("http://127.0.0.1:7002");
        runtimeB.Version.Should().Be("9.8.7");

        // DeleteStatus on A must NOT touch B's files (proves the delete path is also scoped to
        // the manager's own dataDirectory).
        mgrA.DeleteStatus();

        File.Exists(Path.Combine(dirA, statusFile)).Should().BeFalse();
        File.Exists(Path.Combine(dirA, runtimeFile)).Should().BeFalse();
        File.Exists(Path.Combine(dirB, statusFile)).Should().BeTrue("deleting A's status must not remove B's files");
        File.Exists(Path.Combine(dirB, runtimeFile)).Should().BeTrue();
    }
}
