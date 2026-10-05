using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What the Android home screen shows, worked out from one <see cref="BlindAppStatus"/> (the UI-safe snapshot of <see cref="IBlindAppController"/>)
/// and nothing else: the page only assigns these values to its controls. Every state and error sentence of the screen is here, so that it is
/// tested without a phone and cannot drift from the controller's policy (a secret store that does not answer is "key storage unavailable",
/// never a lost key and never an exception on the screen).
/// </summary>
public sealed record BlindHomeView(
    string Name, string Node, string Pair, string Load, string Sync, string Backup,
    string? Job, double? JobProgress,
    string? PairingCode, bool ShowPairingCode, string PairingMessage, bool CanConnect, bool CanRePair,
    bool CanSyncNow, bool CanBackupNow, string BackupNowText, bool CanSaveTo,
    string? KeyLostText, string? KeyStoreText, string? StartErrorText,
    int ScheduleIndex, string Backups, string Log)
{
    public static readonly BlindBackupSchedule[] Schedules = [BlindBackupSchedule.Off, BlindBackupSchedule.Daily, BlindBackupSchedule.Weekly];

    public const string KeyLostMessage =
        "This phone's backup key is lost, so it cannot make backups the computer could open. Use Disconnect and wipe below, then pair the phone again.";

    /// <param name="status">The controller's status.</param>
    /// <param name="pairingCode">The controller's <see cref="IBlindAppController.PairingCode"/> (only asked for while an answer is awaited).</param>
    /// <param name="backupRunning">True while a first load or backup runs in the foreground service or the scheduled worker (<see cref="BlindActivity"/>):
    /// "Back up now" is then off and says so, so that a second tap cannot start a second service job.</param>
    public static BlindHomeView Build(BlindAppStatus status, string? pairingCode, bool backupRunning)
    {
        var keyStoreDown = status.KeyStoreUnavailable is not null;
        var job = status.ActiveJob is not null
            ? (status.JobProgress is { } p ? $"{status.ActiveJob}: {p:P0}" : status.ActiveJob)
            : backupRunning ? "Working in the background" : null;

        return new BlindHomeView(
            Name: status.DisplayName ?? "(no name)",
            Node: status.NodeId is { } id ? $"Node {id}" : "No identity",
            Pair: status.IsPaired ? $"Calls {status.Endpoint}" : "Not paired yet: do steps 1 and 2 below.",
            Load: LoadText(status),
            Sync: $"Last sync: {When(status.LastSyncAt)}",
            Backup: $"Last backup: {When(status.LastBackupAt)}",
            Job: job,
            JobProgress: status.JobProgress,
            PairingCode: pairingCode,
            ShowPairingCode: pairingCode is not null,
            PairingMessage: pairingCode is not null ? ""
                : keyStoreDown ? "The pairing code cannot be shown while the key storage of this phone does not answer."
                : status.StartError is not null ? "The phone could not be set up; see the message below."
                : status.IsPaired ? "Paired. The code was used up; to connect this phone to another computer, choose Re-pair."
                : "No identity. Wipe and set the phone up again.",
            CanConnect: pairingCode is not null,
            CanRePair: pairingCode is null && status.IsPaired && !keyStoreDown,
            CanSyncNow: status.IsPaired && status.InitialLoadDone,
            CanBackupNow: status.IsPaired && !status.BackupKeyLost && !keyStoreDown && !backupRunning && status.ActiveJob is null,
            BackupNowText: backupRunning || status.ActiveJob is not null ? "Backup is running…" : "Back up now",
            CanSaveTo: status.Backups.Count > 0,
            KeyLostText: status.BackupKeyLost ? KeyLostMessage : null,
            KeyStoreText: status.KeyStoreUnavailable is { } reason
                ? $"The key storage of this phone did not answer ({reason}). Nothing was changed and no key is lost. Try again in a moment; if it stays, close the app and open it again. If it still stays, the key may have been invalidated (for example after a change of the screen lock): choose Disconnect and wipe below, then pair the phone again."
                : null,
            StartErrorText: status.StartError is { } error
                ? $"Could not set the phone up: {error} Close the app and open it again; if this stays, choose Disconnect and wipe."
                : null,
            ScheduleIndex: Math.Max(0, Array.IndexOf(Schedules, status.Schedule)),
            Backups: status.Backups.Count == 0
                ? "No backups on the phone yet."
                : string.Join("\n", status.Backups.Select(b => $"{b.Name}  ({b.Size / 1024} KiB)")),
            Log: string.Join("\n", status.RecentLog.Select(e => $"{e.At.ToLocalTime():dd.MM HH:mm}  {e.Message}")));
    }

    /// <summary>The screen when even the status could not be read: a sentence, and the way out.</summary>
    public static string StatusFailedText(Exception ex) =>
        $"Could not read the state of the phone: {ex.Message} Close the app and open it again; if this stays, choose Disconnect and wipe.";

    private static string LoadText(BlindAppStatus status)
    {
        if (status.InitialLoadDone) return "First load: done.";
        if (!status.IsPaired) return "First load: starts after pairing.";
        if (status.ActiveJob == "First load")
            return status.JobProgress is { } progress ? $"First load: running ({progress:P0})." : "First load: running.";
        if (status.LastFailure is { Title: "First load" } failure)
        {
            var text = $"First load: the last try failed - {failure.Message.ReplaceLineEndings(" ").Trim()}";
            return failure.Attempts > 1 ? $"{text} ({failure.Attempts} tries in a row). It tries again soon." : $"{text}. It tries again soon.";
        }
        return "First load: starting.";
    }

    private static string When(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "never";
}
