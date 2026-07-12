namespace BeeMemoryBank.Hosting;

/// <summary>
/// Status codes for the result of reading a ready file.
/// </summary>
public enum ReadyFileReadStatus
{
    /// <summary>
    /// The file was successfully read and parsed.
    /// </summary>
    Success,

    /// <summary>
    /// The ready file was not found at the specified path.
    /// </summary>
    FileNotFound,

    /// <summary>
    /// The file exists, but its content is not a valid JSON representation of <see cref="ReadyFileInfo"/>.
    /// </summary>
    CorruptedJson,

    /// <summary>
    /// An I/O error or other unexpected error occurred during reading.
    /// </summary>
    ReadError
}
