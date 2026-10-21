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
    int ScheduleIndex, string Backups, string Log, string Store = "", string? Problem = null)
{
    public static readonly BlindBackupSchedule[] Schedules = [BlindBackupSchedule.Off, BlindBackupSchedule.Daily, BlindBackupSchedule.Weekly];

    public const string KeyLostMessage =
        "This phone's backup key is lost, so it cannot make backups the computer could open. Remove this account from the accounts list, then add it again.";

    /// <param name="status">The controller's status.</param>
    /// <param name="pairingCode">The controller's <see cref="IBlindAppController.PairingCode"/> (only asked for while an answer is awaited).</param>
    /// <param name="store">What <see cref="BlindStoreStats.GetAsync"/> last answered; null is "unknown" (see <see cref="StoreText"/>).</param>
    /// <param name="now">The time the age of the restore point is told against; the clock when not given.</param>
    /// <param name="backupRunning">True while a first load or backup runs in the foreground service or the scheduled worker (<see cref="BlindActivity"/>):
    /// "Back up now" is then off and says so, so that a second tap cannot start a second service job.</param>
    public static BlindHomeView Build(
        BlindAppStatus status, string? pairingCode, bool backupRunning, BlindStoreInfo? store = null, DateTimeOffset? now = null)
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
            Sync: $"Last sync: {When(status.LastSyncAt)}" + (BlindSourceRemoval.RefusalIsRecent(status.SourceRefusedAt, now ?? DateTimeOffset.UtcNow) ? $". {BlindSourceRemoval.FirstRefusalSentence}" : ""),
            Backup: $"Last backup: {When(status.LastBackupAt)}",
            Job: job,
            JobProgress: status.JobProgress,
            PairingCode: pairingCode,
            ShowPairingCode: pairingCode is not null,
            PairingMessage: pairingCode is not null ? ""
                : keyStoreDown ? "The pairing code cannot be shown while the key storage of this phone does not answer."
                : status.StartError is not null ? "The phone could not be set up; see the message below."
                : status.IsPaired ? "Paired. The code was used up; to connect this phone to another computer, choose Re-pair."
                : "No identity. Remove this account from the accounts list and add it again.",
            CanConnect: pairingCode is not null,
            CanRePair: pairingCode is null && status.IsPaired && !keyStoreDown,
            // Off while a job runs: a second round asked for next to the first is refused by the loop anyway, and a button that stays on says that it can be used.
            CanSyncNow: status.IsPaired && status.InitialLoadDone && status.ActiveJob is null,
            CanBackupNow: status.IsPaired && !status.BackupKeyLost && !keyStoreDown && !backupRunning && status.ActiveJob is null,
            BackupNowText: backupRunning || status.ActiveJob is not null ? "Backup is running…" : "Back up now",
            CanSaveTo: status.Backups.Count > 0,
            KeyLostText: status.BackupKeyLost ? KeyLostMessage : null,
            KeyStoreText: status.KeyStoreUnavailable is { } reason
                ? $"The key storage of this phone did not answer ({BlindFailureText.ForDisplay(reason)}). Nothing was changed and no key is lost. Try again in a moment; if it stays, close the app and open it again. If it still stays, the key may have been invalidated (for example after a change of the screen lock): remove this account from the accounts list and add it again."
                : null,
            StartErrorText: status.StartError is { } error
                ? $"Could not set the phone up: {BlindFailureText.ForDisplay(error)} Close the app and open it again; if this stays, remove this account from the accounts list and add it again."
                : null,
            ScheduleIndex: Math.Max(0, Array.IndexOf(Schedules, status.Schedule)),
            Backups: status.Backups.Count == 0
                ? "No backups on the phone yet."
                : string.Join("\n", status.Backups.Select(b => $"{b.Name}  ({b.Size / 1024} KiB)")),
            Log: string.Join("\n", status.RecentLog.Select(e => $"{e.At.ToLocalTime():dd.MM HH:mm}  {Stored(e)}")),
            Store: StoreText(store, status, now ?? DateTimeOffset.UtcNow),
            Problem: ProblemText(status));
    }

    /// <summary>The log kinds of a failed contact with the node: a sync round, a refused pin, a refused certificate.</summary>
    private static readonly string[] ContactProblems = ["sync", "pin", "certificate"];

    /// <summary>How far apart a refused key and the failed sync it caused can be logged.</summary>
    private static readonly TimeSpan SameRound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The newest problem the copy has now, or null: a failed first load or backup (<see cref="BlindAppStatus.LastFailure"/>), or a failed contact with the node that is newer than the
    /// last successful sync. A refused pin or certificate is logged a moment before the sync failure it causes, and says more: it is shown instead. The full story is in the log.
    /// </summary>
    public static string? ProblemText(BlindAppStatus status)
    {
        var problems = status.RecentLog
            .Where(e => ContactProblems.Contains(e.Kind) && (status.LastSyncAt is not { } ok || e.At > ok))
            .OrderByDescending(e => e.At)
            .ToList();
        var contact = problems.FirstOrDefault();
        if (contact is { Kind: "sync" } failed &&
            problems.FirstOrDefault(e => e.Kind != "sync" && failed.At - e.At <= SameRound) is { } cause)
            contact = cause;
        var heavy = status.LastFailure;
        if (contact is null && heavy is null) return null;
        if (heavy is not null && (contact is null || heavy.At >= contact.At))
            return $"Problem ({heavy.At.ToLocalTime():dd.MM HH:mm}): {heavy.Title} failed - {BlindFailureText.ForDisplay(heavy.Message)}" +
                   (heavy.Attempts > 1 ? $" ({heavy.Attempts} tries in a row)." : "");
        return $"Problem ({contact!.At.ToLocalTime():dd.MM HH:mm}): {Stored(contact)}";
    }

    /// <summary>
    /// A line of the log as the screen shows it. The controller's status already holds the lines cleaned (<see cref="BlindPhoneLog.Latest"/>); a status made by hand (a test, a head's own
    /// seam) may hold the raw ones, so the same rule is applied again here - it changes nothing that is clean. There is no bypass for any kind: not even the pairing line is shown unless
    /// it is stamped by this version and has the shape this code writes (<see cref="BlindPhoneLog.Entry.ForDisplay"/>).
    /// </summary>
    private static string Stored(BlindPhoneLog.Entry entry) => entry.ForDisplay().Message;

    /// <summary>
    /// The "store" line of a screen (docs/design/step-a-spec.md 10.2), from <see cref="BlindStoreStats.GetAsync"/>: the age of the restore point, the
    /// changes held since it, the bytes stored - never a note count. Before the first load the copy holds nothing yet, and a summary that could not
    /// be read is "unknown", never zero.
    /// </summary>
    public static string StoreText(BlindStoreInfo? store, BlindAppStatus status, DateTimeOffset now)
    {
        if (!status.InitialLoadDone) return "Restore point: none yet (the first load is not done).";
        if (store is null) return "Restore point: unknown (the database could not be read just now).";
        var point = store.RestorePointAt is { } created
            ? $"made {Ago(now - new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)))}"
            : "none yet";
        return $"Restore point: {point}. Changes since: {store.ChangesSince}. Stored: {Size(store.StoredBytes)}.";
    }

    private static string Ago(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} d ago";

    private static string Size(long bytes) =>
        bytes < 1L << 20 ? $"{Math.Max(1, bytes >> 10)} KiB"
        : bytes < 1L << 30 ? $"{bytes / (double)(1L << 20):0.#} MiB"
        : $"{bytes / (double)(1L << 30):0.#} GiB";

    /// <summary>
    /// The screen when even the status could not be read: a sentence, and the way out. The failure is told by its KIND (<see cref="BlindFailureText"/>), never by its message: the message of
    /// an exception that came through the HTTP stack carries the address that was dialled or a redirect target the other side chose.
    /// </summary>
    public static string StatusFailedText(Exception ex)
    {
        var said = BlindFailureText.Say(ex);
        if (!said.EndsWith('.')) said += ".";
        return $"Could not read the state of the phone: {said} Close the app and open it again; if this stays, remove this account from the accounts list and add it again.";
    }

    private static string LoadText(BlindAppStatus status)
    {
        if (status.InitialLoadDone) return "First load: done.";
        if (!status.IsPaired) return "First load: starts after pairing.";
        if (status.ActiveJob == "First load")
            return status.JobProgress is { } progress ? $"First load: running ({progress:P0})." : "First load: running.";
        if (status.LastFailure is { Title: "First load" } failure)
        {
            var text = $"First load: the last try failed - {BlindFailureText.ForDisplay(failure.Message)}";
            return failure.Attempts > 1 ? $"{text} ({failure.Attempts} tries in a row). It tries again soon." : $"{text}. It tries again soon.";
        }
        return "First load: starting.";
    }

    private static string When(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "never";
}
