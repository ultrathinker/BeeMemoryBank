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
}
