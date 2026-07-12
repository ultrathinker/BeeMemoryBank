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
/// Manages the orchestrator's state/status file.
/// </summary>
public class NodeStatusManager
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statusFilePath;

    public NodeStatusManager(string dataDirectory)
    {
        _statusFilePath = Path.Combine(dataDirectory, "node.status.json");
    }

    /// <summary>
    /// Writes the ready-state of all processes to node.status.json.
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
    }

    /// <summary>
    /// Deletes the status file upon clean shutdown.
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
    }
}
