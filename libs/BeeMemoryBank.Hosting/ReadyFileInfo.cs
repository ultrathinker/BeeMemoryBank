using System.Text.Json.Serialization;

namespace BeeMemoryBank.Hosting;

/// <summary>
/// Contains information about a started process, such as its PID, URLs, application name, version, and startup time.
/// </summary>
public record ReadyFileInfo(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("urls")] IReadOnlyList<string> Urls,
    [property: JsonPropertyName("applicationName")] string ApplicationName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("startupTimeUtc")] DateTime StartupTimeUtc
);
