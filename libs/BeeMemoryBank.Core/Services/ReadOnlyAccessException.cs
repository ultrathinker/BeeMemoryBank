namespace BeeMemoryBank.Core.Services;

// Thrown by repository-level write guards when the caller has read access
// (no deny, allow-ACL match) but the matching allow-entry is is_read_only=1.
// Subclasses UnauthorizedAccessException so legacy catch handlers still work,
// while new catch handlers can match this specific type for a clearer message.
public class ReadOnlyAccessException : UnauthorizedAccessException
{
    public string Path { get; }

    public ReadOnlyAccessException(string path)
        : base($"Path '{path}' is read-only for this caller")
    {
        Path = path;
    }
}
