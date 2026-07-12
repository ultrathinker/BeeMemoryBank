namespace BeeMemoryBank.Node;

/// <summary>
/// Defines the startup configuration for a child process managed by the orchestrator.
/// </summary>
public record ChildProcessConfig(
    string ApplicationName,
    string ExecutablePath,
    string WorkingDirectory,
    string ReadyFilePath,
    string? Arguments = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null
);
