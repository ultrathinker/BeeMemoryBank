using System.Text.Json;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// Loads and saves <see cref="BlindBackupSettings"/> as a single JSON file under
/// <c>{dataPath}/blind/</c>. One instance per write; every read goes to disk so two processes
/// (Api and a concurrent CLI call through it) can never act on stale settings. Writes are atomic
/// (temp file + File.Move) — a node that loses power mid-save keeps the previous settings, which
/// is the direction that keeps backups running.
/// </summary>
public sealed class BlindBackupSettingsStore(string dataPath)
{
    private readonly string _dir = Path.Combine(dataPath, "blind");

    /// <summary>The node's data path — what repository locations are checked against.</summary>
    public string DataPath => dataPath;
    private string FilePath => Path.Combine(_dir, "settings.json");

    public BlindBackupSettings Load()
    {
        if (!File.Exists(FilePath)) return new BlindBackupSettings();
        try
        {
            return JsonSerializer.Deserialize<BlindBackupSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException)
        {
            // Unreadable settings fall back to defaults rather than crashing the node: an operator
            // can then repair the configuration through the console instead of losing the node.
            return new BlindBackupSettings();
        }
    }

    // Saves and the wipe's delete are serialized: a save racing the wipe could otherwise
    // recreate settings.json (or its .tmp) right after the wipe removed it.
    private readonly object _write = new();
    private bool _closed;

    public void Save(BlindBackupSettings settings)
    {
        // No Validate() here on purpose: configuration arrives in pieces (repository first,
        // password later, delivered by the pairing flow). The PUT handler validates what it saves.
        lock (_write)
        {
            if (_closed) throw new SettingsClosedException();
            Directory.CreateDirectory(_dir);
            var tmp = FilePath + ".tmp";
            // 0600 from creation, the temp file included: both carry the restic password in the
            // clear (by design, plan §7), and a chmod after the write leaves a window.
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var w = new StreamWriter(tmp, System.Text.Encoding.UTF8, options))
                w.Write(JsonSerializer.Serialize(settings, BlindBackupSettings.JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>
    /// The wipe's half: refuses every save from now until the returned handle is disposed, then
    /// deletes the settings file and any temp file a torn save left behind — one lock, so no
    /// save can land between the check and the delete.
    /// </summary>
    public IDisposable CloseAndDelete()
    {
        lock (_write)
        {
            _closed = true;
            File.Delete(FilePath);
            File.Delete(FilePath + ".tmp");
        }
        return new Reopen(this);
    }

    private sealed class Reopen(BlindBackupSettingsStore store) : IDisposable
    {
        public void Dispose()
        {
            lock (store._write) store._closed = false;
        }
    }

    /// <summary>
    /// The one secret-bearing call surface: returns a copy with credentials masked, for the
    /// settings-reading endpoints. The restic and S3 secrets never travel back to the console.
    /// </summary>
    public BlindBackupSettings MaskedForDisplay(BlindBackupSettings s) => new()
    {
        RepoType = s.RepoType,
        RepoFolder = s.RepoFolder,
        S3Endpoint = s.S3Endpoint,
        S3Bucket = s.S3Bucket,
        S3Prefix = s.S3Prefix,
        S3Region = s.S3Region,
        S3AccessKey = string.IsNullOrEmpty(s.S3AccessKey) ? null : "••••",
        S3SecretKey = string.IsNullOrEmpty(s.S3SecretKey) ? null : "••••",
        ResticPassword = string.IsNullOrEmpty(s.ResticPassword) ? null : "••••",
        KeepDaily = s.KeepDaily,
        KeepWeekly = s.KeepWeekly,
        KeepMonthly = s.KeepMonthly,
        KeepYearly = s.KeepYearly,
        ScheduleTime = s.ScheduleTime,
        // The display answers "will this node back itself up?", not "has the operator decided?":
        // an undecided schedule that runs is shown as on, because that is what the node does. The
        // console sends the toggle back only when the operator actually flipped it, so showing the
        // effective value does not turn it into a decision (see the console's save).
        ScheduleEnabled = s.ScheduleRuns(dataPath),
        CheckSubsetPercent = s.CheckSubsetPercent,
        ResticGoMemLimit = s.ResticGoMemLimit,
        ResticBinary = s.ResticBinary,
    };
}

/// <summary>A save arrived while the wipe holds the settings; nothing was written.</summary>
public sealed class SettingsClosedException() : InvalidOperationException("the node is being wiped");
