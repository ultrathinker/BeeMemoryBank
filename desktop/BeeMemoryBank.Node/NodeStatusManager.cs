using System.Text.Json;
using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Node;

/// <summary>
/// Represents the serialized overall status of the node orchestrator.
/// </summary>
public record NodeStatus(
    int Pid,
    string Status,
    DateTime StartupTimeUtc,
    IReadOnlyDictionary<string, ChildNodeStatus> Children
);

/// <summary>
/// Represents the status info of a specific child node process.
/// </summary>
public record ChildNodeStatus(
    int Pid,
    IReadOnlyList<string> Urls,
    string Version
);

/// <summary>
/// Represents the serialized lightweight runtime descriptor of the node.
/// </summary>
public record RuntimeDescriptor(
    int Pid,
    string? FrontUrl,
    string Version,
    string Mode
);

/// <summary>
/// Manages the orchestrator's state/status file.
/// </summary>
public class NodeStatusManager
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statusFilePath;
    private readonly string _runtimeFilePath;
    private readonly string? _frontUrl;
    private readonly string _version;
    private readonly string _mode;

    public NodeStatusManager(
        string dataDirectory,
        string? frontUrl = null,
        string version = "1.0.1",
        string mode = "production")
    {
        _statusFilePath = Path.Combine(dataDirectory, "node.status.json");
        _runtimeFilePath = Path.Combine(dataDirectory, ".runtime.json");
        _frontUrl = frontUrl;
        _version = version;
        _mode = mode;
    }

    /// <summary>
    /// Writes the ready-state of all processes to node.status.json and .runtime.json.
    /// </summary>
    public void WriteStatus(IReadOnlyDictionary<string, ReadyFileInfo> childrenInfos)
    {
        var children = childrenInfos.ToDictionary(
            kvp => kvp.Key,
            kvp => new ChildNodeStatus(kvp.Value.Pid, kvp.Value.Urls, kvp.Value.Version)
        );

        var status = new NodeStatus(
            Pid: Environment.ProcessId,
            Status: "Ready",
            StartupTimeUtc: DateTime.UtcNow,
            Children: children
        );

        try
        {
            var json = JsonSerializer.Serialize(status, JsonOpts);
            File.WriteAllText(_statusFilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NodeStatusManager] Failed to write status file: {ex.Message}");
        }

        try
        {
            var runtime = new RuntimeDescriptor(
                Pid: Environment.ProcessId,
                FrontUrl: _frontUrl,
                Version: _version,
                Mode: _mode
            );
            var runtimeJson = JsonSerializer.Serialize(runtime, JsonOpts);
            File.WriteAllText(_runtimeFilePath, runtimeJson);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NodeStatusManager] Failed to write runtime descriptor file: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the status and runtime files upon clean shutdown.
    /// </summary>
    public void DeleteStatus()
    {
        try
        {
            if (File.Exists(_statusFilePath))
            {
                File.Delete(_statusFilePath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NodeStatusManager] Failed to delete status file: {ex.Message}");
        }

        try
        {
            if (File.Exists(_runtimeFilePath))
            {
                File.Delete(_runtimeFilePath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NodeStatusManager] Failed to delete runtime descriptor file: {ex.Message}");
        }
    }
}
