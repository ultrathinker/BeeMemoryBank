using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Reads and writes single flags in desktop-settings.json. Every write re-reads the file and
/// replaces only its own key, so settings owned by different services (prevent sleep, update
/// checks, ...) never overwrite each other.
/// </summary>
public sealed class DesktopSettingsStore
{
    private static readonly object FileLock = new();
    private readonly string _path;

    public DesktopSettingsStore(string? path = null)
    {
        _path = path ?? BeeMemoryBank.AppPaths.BmbPaths.DesktopSettingsFile;
    }

    public bool GetBool(string key, bool defaultValue)
    {
        lock (FileLock)
        {
            var node = Load()[key];
            return node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : defaultValue;
        }
    }

    public void SetBool(string key, bool value)
    {
        lock (FileLock)
        {
            var root = Load();
            root[key] = value;
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _path, overwrite: true);
        }
    }

    private JsonObject Load()
    {
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject obj)
            {
                return obj;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[DesktopSettingsStore] Could not read {_path}: {ex.Message}");
        }
        return new JsonObject();
    }
}
