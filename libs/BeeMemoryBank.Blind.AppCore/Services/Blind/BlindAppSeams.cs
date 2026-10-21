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

    /// <summary>
    /// Called when "Disconnect and wipe" gave up because the running work did not stop in time and nothing was deleted: the host puts back what
    /// <see cref="StopBackgroundWork"/> took away (Android: the periodic WorkManager jobs). The default does nothing (a host whose stop is
    /// not undone, or that re-schedules by itself).
    /// </summary>
    void ResumeBackgroundWork() { }
}
public interface IBlindBackupExporter { Task<Stream> CreateAsync(string suggestedName, CancellationToken ct); }

/// <summary>
/// What a host tells <c>AddBlindAppCore</c>: its private root and database, a clock, and how to name this device when the
/// identity is made (null: "Blind copy").
/// </summary>
public sealed record BlindAppOptions(string DataDirectory, string DatabasePath, TimeProvider? TimeProvider = null,
    Func<string>? DisplayNameFactory = null)
{
    // Init properties, not constructor parameters: a head calls the constructor above, and a parameter of a new type there would put that type in the
    // head's own metadata (the heads' reference tests name every AppCore type a head may touch).

    /// <summary>The lock for a long job (first load, backup) that every account's runtime of this device shares; null gives the composition a gate of its own (a copy with one account).</summary>
    public BlindDeviceGate? DeviceGate { get; init; }

    /// <summary>The id of the account this runtime is, and the name its card shows (read when asked): what the device's gate records when this account's long job holds it. Null for a copy with one account.</summary>
    public string? DeviceOwnerId { get; init; }

    public Func<string>? DeviceOwnerName { get; init; }

    /// <summary>
    /// Tests only: the handler at the bottom of the account's HTTP pipeline, in place of the pinned or public-CA socket handler. Everything above it
    /// (https only, the origin check, no redirect, maintenance detection) is the real pipeline.
    /// </summary>
    public Func<HttpMessageHandler>? PrimaryHandlerFactory { get; init; }
}

internal sealed class BlindAppPaths(string dataDirectory, string databasePath) : IBlindPaths
{
    public string DataDirectory => dataDirectory;
    public string DatabasePath => databasePath;
}
