using System.Collections.Concurrent;

namespace BeeMemoryBank.Api.Services;

public class DownloadTokenService
{
    private readonly ConcurrentDictionary<string, DownloadEntry> _entries = new();

    public record DownloadEntry(string FilePath, string FileName, DateTime CreatedAt);

    public string Register(string filePath, string fileName)
    {
        var token = Guid.NewGuid().ToString("N");
        _entries[token] = new DownloadEntry(filePath, fileName, DateTime.UtcNow);
        return token;
    }

    public DownloadEntry? Take(string token)
    {
        _entries.TryRemove(token, out var entry);
        return entry;
    }

    public List<DownloadEntry> CleanupExpired(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var expired = new List<DownloadEntry>();

        foreach (var kvp in _entries)
        {
            if (kvp.Value.CreatedAt < cutoff)
            {
                if (_entries.TryRemove(kvp.Key, out var entry))
                    expired.Add(entry);
            }
        }

        return expired;
    }
}
