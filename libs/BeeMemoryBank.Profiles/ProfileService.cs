using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.AppPaths;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Profiles;

/// <summary>
/// Service that manages the registry of profile storages in the profiles.json file.
/// </summary>
public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;
    private readonly string? _defaultVaultDir;
    private readonly string? _vaultsParentDir;
    private readonly ILogger<ProfileService>? _logger;
    private readonly object _lock = new();
    private ProfilesRegistry _registry = null!;

    /// <summary>
    /// Gets the identifier of the last used profile.
    /// </summary>
    public string? LastUsedProfileId
    {
        get
        {
            lock (_lock)
            {
                return _registry.LastUsedProfileId;
            }
        }
    }

    /// <summary>
    /// Gets the current autostart mode.
    /// </summary>
    public AutostartMode AutostartMode
    {
        get
        {
            lock (_lock)
            {
                return _registry.AutostartMode;
            }
        }
    }

    /// <summary>
    /// Gets the fixed profile identifier for autostart when mode is FixedProfile.
    /// </summary>
    public string? AutostartProfileId
    {
        get
        {
            lock (_lock)
            {
                return _registry.AutostartProfileId;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileService"/> class.
    /// </summary>
    /// <param name="filePath">The absolute path to the profiles.json file.</param>
    /// <param name="defaultVaultDir">The default vault directory. If null, falls back to BmbPaths.DefaultVaultDir.</param>
    /// <param name="vaultsParentDir">The parent directory for new vaults. If null, falls back to BmbPaths.VaultDir.</param>
    /// <param name="logger">Optional logger instance.</param>
    public ProfileService(
        string filePath,
        string? defaultVaultDir = null,
        string? vaultsParentDir = null,
        ILogger<ProfileService>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        _defaultVaultDir = defaultVaultDir != null ? Path.GetFullPath(defaultVaultDir) : null;
        _vaultsParentDir = vaultsParentDir != null ? Path.GetFullPath(vaultsParentDir) : null;
        _logger = logger;

        LoadOrCreateRegistry();
    }

    /// <summary>
    /// Gets a list of all registered profiles.
    /// </summary>
    public IReadOnlyList<ProfileEntry> GetAll()
    {
        lock (_lock)
        {
            return _registry.Profiles.Select(CloneProfile).ToList();
        }
    }

    /// <summary>
    /// Gets a profile by its unique identifier.
    /// </summary>
    /// <param name="id">The profile identifier.</param>
    /// <returns>A copy of the found profile entry.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if no profile is found with the specified identifier.</exception>
    public ProfileEntry GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Profile ID cannot be null or empty.", nameof(id));
        }

        lock (_lock)
        {
            var profile = _registry.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
            {
                throw new KeyNotFoundException($"Profile with ID '{id}' was not found.");
            }
            return CloneProfile(profile);
        }
    }

    /// <summary>
    /// Returns the last used profile, or falls back to the first profile in the list.
    /// This method never throws an exception as long as there is at least one profile.
    /// </summary>
    public ProfileEntry GetLastUsedOrDefault()
    {
        lock (_lock)
        {
            var lastUsedId = _registry.LastUsedProfileId;
            var profile = _registry.Profiles.FirstOrDefault(p => p.Id == lastUsedId);
            profile ??= _registry.Profiles.First(); // Safe because Profiles is guaranteed to have at least one element.
            return CloneProfile(profile);
        }
    }

    /// <summary>
    /// Adds a new profile to the registry.
    /// </summary>
    /// <param name="name">The display name of the profile.</param>
    /// <param name="dataPath">Optional absolute path to the data directory. If null, a path is automatically generated.</param>
    /// <returns>The created profile entry.</returns>
    public ProfileEntry AddProfile(string name, string? dataPath = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name cannot be null or whitespace.", nameof(name));
        }

        if (dataPath != null)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                throw new ArgumentException("Data path cannot be empty or whitespace.", nameof(dataPath));
            }
            if (!Path.IsPathRooted(dataPath))
            {
                throw new ArgumentException("Data path must be an absolute path.", nameof(dataPath));
            }
        }

        lock (_lock)
        {
            // Generate a short 8-hex identifier
            string id = Guid.NewGuid().ToString("N")[..8];

            string finalDataPath;
            if (dataPath != null)
            {
                finalDataPath = Path.GetFullPath(dataPath);
            }
            else
            {
                if (_vaultsParentDir != null)
                {
                    finalDataPath = Path.GetFullPath(Path.Combine(_vaultsParentDir, id));
                    Directory.CreateDirectory(finalDataPath);
                }
                else
                {
                    finalDataPath = BmbPaths.VaultDir(id);
                }
            }

            // Two profiles pointing at the same vault would silently share one DB, one
            // encryption/key-slot state, one auto-unlock file, one node.lock - the switch
            // engine would present them as independent accounts while they are actually the
            // same storage. Only reachable via an explicit dataPath (the auto-generated path
            // is always a fresh per-id subdirectory), but still worth refusing outright.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var collision = _registry.Profiles.FirstOrDefault(p => string.Equals(p.DataPath, finalDataPath, comparison));
            if (collision != null)
            {
                throw new ArgumentException(
                    $"Data path '{finalDataPath}' is already used by profile '{collision.Name}' ({collision.Id}).",
                    nameof(dataPath));
            }

            var entry = new ProfileEntry
            {
                Id = id,
                Name = name.Trim(),
                DataPath = finalDataPath,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow
            };

            _registry.Profiles.Add(entry);
            SaveInternal(_registry, skipBak: false);

            return CloneProfile(entry);
        }
    }

    /// <summary>
    /// Renames an existing profile. Does not move or modify files on disk.
    /// </summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="newName">The new display name for the profile.</param>
    public void RenameProfile(string id, string newName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Profile ID cannot be null or empty.", nameof(id));
        }
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("New name cannot be null or whitespace.", nameof(newName));
        }

        lock (_lock)
        {
            var profile = _registry.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
            {
                throw new KeyNotFoundException($"Profile with ID '{id}' was not found.");
            }

            profile.Name = newName.Trim();
            SaveInternal(_registry, skipBak: false);
        }
    }

    /// <summary>
    /// Removes a profile from the registry.
    /// IMPORTANT: This method only forgets the profile pointer in the registry file.
    /// It DOES NOT touch, delete, or modify any vault files/directories on the filesystem.
    /// </summary>
    /// <param name="id">The identifier of the profile to forget.</param>
    /// <exception cref="InvalidOperationException">Thrown when trying to forget the only remaining profile.</exception>
    public void ForgetProfile(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Profile ID cannot be null or empty.", nameof(id));
        }

        lock (_lock)
        {
            var profile = _registry.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
            {
                throw new KeyNotFoundException($"Profile with ID '{id}' was not found.");
            }

            if (_registry.Profiles.Count <= 1)
            {
                throw new InvalidOperationException("Cannot forget the only remaining profile in the registry.");
            }

            _registry.Profiles.Remove(profile);

            // Fallback for lastUsedProfileId
            if (_registry.LastUsedProfileId == id)
            {
                _registry.LastUsedProfileId = _registry.Profiles[0].Id;
            }

            // Fallback for autostart settings
            if (_registry.AutostartProfileId == id)
            {
                _registry.AutostartMode = AutostartMode.LastUsed;
                _registry.AutostartProfileId = null;
            }

            SaveInternal(_registry, skipBak: false);
        }
    }

    /// <summary>
    /// Updates the last used profile identifier and updates its last used timestamp.
    /// </summary>
    /// <param name="id">The profile identifier.</param>
    public void SetLastUsed(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Profile ID cannot be null or empty.", nameof(id));
        }

        lock (_lock)
        {
            var profile = _registry.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
            {
                throw new KeyNotFoundException($"Profile with ID '{id}' was not found.");
            }

            profile.LastUsedAt = DateTime.UtcNow;
            _registry.LastUsedProfileId = id;

            SaveInternal(_registry, skipBak: false);
        }
    }

    /// <summary>
    /// Sets the autostart settings.
    /// </summary>
    /// <param name="mode">The autostart mode.</param>
    /// <param name="fixedProfileId">The fixed profile ID. Must be provided if mode is FixedProfile.</param>
    public void SetAutostart(AutostartMode mode, string? fixedProfileId = null)
    {
        lock (_lock)
        {
            if (mode == AutostartMode.FixedProfile)
            {
                if (string.IsNullOrWhiteSpace(fixedProfileId))
                {
                    throw new ArgumentException("Fixed profile ID must be specified when autostart mode is FixedProfile.", nameof(fixedProfileId));
                }

                var exists = _registry.Profiles.Any(p => p.Id == fixedProfileId);
                if (!exists)
                {
                    throw new ArgumentException($"Profile with ID '{fixedProfileId}' does not exist.", nameof(fixedProfileId));
                }

                _registry.AutostartMode = AutostartMode.FixedProfile;
                _registry.AutostartProfileId = fixedProfileId;
            }
            else
            {
                _registry.AutostartMode = AutostartMode.LastUsed;
                _registry.AutostartProfileId = null;
            }

            SaveInternal(_registry, skipBak: false);
        }
    }

    private void LoadOrCreateRegistry()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                _registry = CreateDefaultRegistry();
                SaveInternal(_registry, skipBak: true);
                return;
            }

            if (TryLoadRegistry(_filePath, out var loaded) && loaded.IsValid())
            {
                _registry = loaded;
                return;
            }

            // Current file is corrupted or invalid. Try backup file.
            string bakPath = _filePath + ".bak";
            if (File.Exists(bakPath))
            {
                if (TryLoadRegistry(bakPath, out var loadedBak) && loadedBak.IsValid())
                {
                    _logger?.LogWarning("Registry file '{FilePath}' is corrupted or invalid. Restored from backup '{BakPath}'.", _filePath, bakPath);
                    _registry = loadedBak;
                    // Atomically write restored registry to main path to fix it
                    SaveInternal(_registry, skipBak: true);
                    return;
                }
                else
                {
                    _logger?.LogError("Both registry file '{FilePath}' and backup '{BakPath}' are corrupted or invalid.", _filePath, bakPath);
                }
            }
            else
            {
                _logger?.LogWarning("Registry file '{FilePath}' is corrupted or invalid. No backup file found.", _filePath);
            }

            // Re-create from default if both main and backup are invalid
            _registry = CreateDefaultRegistry();
            SaveInternal(_registry, skipBak: true);
        }
    }

    private ProfilesRegistry CreateDefaultRegistry()
    {
        string defaultVault = _defaultVaultDir ?? BmbPaths.DefaultVaultDir;

        // Ensure default directory exists
        Directory.CreateDirectory(defaultVault);

        return new ProfilesRegistry
        {
            SchemaVersion = 1,
            LastUsedProfileId = BmbPaths.DefaultVaultId,
            AutostartMode = AutostartMode.LastUsed,
            AutostartProfileId = null,
            Profiles = new List<ProfileEntry>
            {
                new ProfileEntry
                {
                    Id = BmbPaths.DefaultVaultId,
                    Name = "Личный",
                    DataPath = defaultVault,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow
                }
            }
        };
    }

    private void SaveInternal(ProfilesRegistry registry, bool skipBak)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!skipBak && File.Exists(_filePath))
        {
            // Only overwrite backup if the current main file passes schema check
            if (TryLoadRegistry(_filePath, out var currentLoaded) && currentLoaded.IsValid())
            {
                string bakPath = _filePath + ".bak";
                File.Copy(_filePath, bakPath, overwrite: true);
            }
        }

        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(registry, JsonOpts);
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true); // Force flush to physical storage medium
            }

            File.Move(tempPath, _filePath, overwrite: true);
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
                    // Ignore exception in finally block to avoid masking primary exceptions
                }
            }
        }
    }

    private bool TryLoadRegistry(string path, [NotNullWhen(true)] out ProfilesRegistry? registry)
    {
        registry = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            registry = JsonSerializer.Deserialize<ProfilesRegistry>(bytes, JsonOpts);
            return registry != null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load/parse profiles registry from '{Path}'.", path);
            return false;
        }
    }

    private static ProfileEntry CloneProfile(ProfileEntry entry)
    {
        return new ProfileEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            DataPath = entry.DataPath,
            CreatedAt = entry.CreatedAt,
            LastUsedAt = entry.LastUsedAt
        };
    }
}
