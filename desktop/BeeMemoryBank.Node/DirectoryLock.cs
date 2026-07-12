using System.IO;

namespace BeeMemoryBank.Node;

/// <summary>
/// Manages acquiring and holding an exclusive OS-level lock on the data directory.
/// </summary>
public sealed class DirectoryLock : IDisposable
{
    private readonly FileStream _lockStream;
    private readonly string _lockFilePath;

    private DirectoryLock(FileStream lockStream, string lockFilePath)
    {
        _lockStream = lockStream;
        _lockFilePath = lockFilePath;
    }

    /// <summary>
    /// Attempts to acquire an exclusive lock on the specified directory.
    /// Throws InvalidOperationException if the lock cannot be acquired.
    /// </summary>
    public static DirectoryLock Acquire(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Data directory path cannot be null or whitespace.", nameof(dataDirectory));
        }

        try
        {
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not create or access data directory '{dataDirectory}'.", ex);
        }

        var lockFilePath = Path.Combine(dataDirectory, "node.lock");

        try
        {
            // Open with FileShare.None to prevent any other process/thread from accessing it.
            // FileOptions.DeleteOnClose guarantees that the file is deleted automatically on close or exit.
            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return new DirectoryLock(stream, lockFilePath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Could not acquire directory lock on '{dataDirectory}'. Another instance of the orchestrator is likely running.", 
                ex);
        }
    }

    public void Dispose()
    {
        _lockStream.Dispose();
    }
}
