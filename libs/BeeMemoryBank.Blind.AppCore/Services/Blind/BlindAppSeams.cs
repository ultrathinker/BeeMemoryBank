using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Host-owned persistent state. The Android adapter commits each SharedPreferences write.</summary>
public interface IBlindStateStore : IBlindPhoneStore
{
    /// <summary>
    /// Called by "Disconnect and wipe" after every key was set to null: a host that keeps the state in a file removes that file (and any
    /// leftover of its atomic writes) so that nothing of the blind copy remains. The default does nothing (Android's preferences are
    /// already empty).
    /// </summary>
    void Erase() { }
}

/// <summary>Host-owned blind secrets, including the external identity seed.</summary>
public interface IBlindSecretStore : IBlindPhoneKeys
{
    byte[]? LoadIdentitySeed();
}

public interface IBlindScheduler { void EnsureScheduled(); void Cancel(); }
public interface IBlindNotifications { void Show(string title, double progress); }

public interface IBlindAutostart
{
    void SetEnabled(bool enabled);

    /// <summary>Whether the app starts with the user's session now; null when the host cannot tell (a screen shows the toggle as unknown).</summary>
    bool? IsEnabled => null;
}

public interface IBlindPaths { string DataDirectory { get; } string DatabasePath { get; } }
public interface IBlindLifecycle
{
    void StopBackgroundWork();
    void StopBackupService();
    void RestartAfterWipe();
}
public interface IBlindBackupExporter { Task<Stream> CreateAsync(string suggestedName, CancellationToken ct); }

/// <summary>
/// What a host tells <c>AddBlindAppCore</c>: its private root and database, a clock, and how to name this device when the
/// identity is made (null: "Blind copy").
/// </summary>
public sealed record BlindAppOptions(string DataDirectory, string DatabasePath, TimeProvider? TimeProvider = null,
    Func<string>? DisplayNameFactory = null);

internal sealed class BlindAppPaths(string dataDirectory, string databasePath) : IBlindPaths
{
    public string DataDirectory => dataDirectory;
    public string DatabasePath => databasePath;
}
