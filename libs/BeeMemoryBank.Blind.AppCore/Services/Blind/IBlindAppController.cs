using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What a host screen and a host scheduler use of the blind app. <see cref="BlindAppController"/> is the implementation; a
/// host (and its tests) depends on this, so that the screen and the scheduler can be exercised with a fake app.
/// </summary>
public interface IBlindAppController
{
    /// <summary>Raised when something a screen shows changed. May come from any thread.</summary>
    event Action? Changed;

    Task InitializeAsync(CancellationToken ct = default);
    BlindAppStatus GetStatus();
    string? PairingCode();
    string? AcceptCallCode(string text);
    void StartRePair();
    void SetSchedule(BlindBackupSchedule schedule);
    Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default);
    Task<string> RequestSyncAsync(CancellationToken ct = default);
    Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default);
    Task DisconnectAndWipeAsync(CancellationToken ct = default);
}
