namespace BeeMemoryBank.Hosting;

/// <summary>
/// Represents the result of reading a process ready file.
/// </summary>
public record ReadyFileReadResult(
    bool Success,
    ReadyFileInfo? Info,
    ReadyFileReadStatus Status,
    string? ErrorMessage = null
);
