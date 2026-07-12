using System.Text.Json;

namespace BeeMemoryBank.Hosting;

/// <summary>
/// Provides methods to write and read process ready files atomically and robustly.
/// </summary>
public static class ReadyFileManager
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Writes the process ready information to the specified file path atomically.
    /// </summary>
    public static void Write(string filePath, ReadyFileInfo info)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(info, JsonOpts);
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true); // Force flush to physical disk
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore exception in finally block to prevent masking the primary exception
                }
            }
        }
    }

    /// <summary>
    /// Writes the process ready information to the specified file path atomically and asynchronously.
    /// </summary>
    public static async Task WriteAsync(string filePath, ReadyFileInfo info, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(info, JsonOpts);
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await fs.WriteAsync(bytes, cancellationToken);
                await fs.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore exception in finally block
                }
            }
        }
    }

    /// <summary>
    /// Reads and parses the ready file from the specified path robustly without throwing exceptions.
    /// </summary>
    public static ReadyFileReadResult Read(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new ReadyFileReadResult(false, null, ReadyFileReadStatus.FileNotFound, "Ready file does not exist.");
            }

            var bytes = File.ReadAllBytes(filePath);
            var info = JsonSerializer.Deserialize<ReadyFileInfo>(bytes, JsonOpts);
            if (info == null)
            {
                return new ReadyFileReadResult(false, null, ReadyFileReadStatus.CorruptedJson, "Deserialization returned null.");
            }

            if (string.IsNullOrEmpty(info.ApplicationName) || string.IsNullOrEmpty(info.Version) || info.Urls == null)
            {
                return new ReadyFileReadResult(false, null, ReadyFileReadStatus.CorruptedJson, "Required fields are missing or invalid in JSON.");
            }

            return new ReadyFileReadResult(true, info, ReadyFileReadStatus.Success);
        }
        catch (JsonException ex)
        {
            return new ReadyFileReadResult(false, null, ReadyFileReadStatus.CorruptedJson, $"Invalid JSON structure: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new ReadyFileReadResult(false, null, ReadyFileReadStatus.ReadError, $"I/O error reading file: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ReadyFileReadResult(false, null, ReadyFileReadStatus.ReadError, $"Unexpected error reading file: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads and parses the ready file from the specified path robustly and asynchronously without throwing exceptions.
    /// </summary>
    public static async Task<ReadyFileReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new ReadyFileReadResult(false, null, ReadyFileReadStatus.FileNotFound, "Ready file does not exist.");
            }

            byte[] bytes;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
            {
                bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes, cancellationToken);
            }

            var info = JsonSerializer.Deserialize<ReadyFileInfo>(bytes, JsonOpts);
            if (info == null)
            {
                return new ReadyFileReadResult(false, null, ReadyFileReadStatus.CorruptedJson, "Deserialization returned null.");
            }

            if (string.IsNullOrEmpty(info.ApplicationName) || string.IsNullOrEmpty(info.Version) || info.Urls == null)
            {
                return new ReadyFileReadResult(false, null, ReadyFileReadStatus.CorruptedJson, "Required fields are missing or invalid in JSON.");
            }

            return new ReadyFileReadResult(true, info, ReadyFileReadStatus.Success);
        }
        catch (JsonException ex)
        {
            return new ReadyFileReadResult(false, null, ReadyFileReadStatus.CorruptedJson, $"Invalid JSON structure: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new ReadyFileReadResult(false, null, ReadyFileReadStatus.ReadError, $"I/O error reading file: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ReadyFileReadResult(false, null, ReadyFileReadStatus.ReadError, $"Unexpected error reading file: {ex.Message}");
        }
    }
}
