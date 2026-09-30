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

    // Unreadable settings fall back to defaults rather than crashing the node: an operator can then
    // repair the configuration through the console instead of losing the node.
    public BlindBackupSettings Load() => TryLoad(out var settings) ? settings : new BlindBackupSettings();

    /// <summary>False when the file exists but cannot be read; the settings are then the defaults.</summary>
    public bool TryLoad(out BlindBackupSettings settings)
    {
        settings = new BlindBackupSettings();
        if (!File.Exists(FilePath)) return true;
        try
        {
            settings = JsonSerializer.Deserialize<BlindBackupSettings>(File.ReadAllText(FilePath)) ?? new();
            if (settings.RepoInUse && settings.RepoInUseKey is null) settings.RepoInUseKey = settings.RepositoryKey(); // first build's flag
            settings.RepoInUse = false;
            return true;
        }
        catch (JsonException)
        {
            return false;
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
            // "A backup has used the repository" and "a remote repository was configured" only ever turn
            // on: a write that read the file before the first backup finished must not turn them back off
            // (the console password default would then move the password of a repository that exists).
            var onDisk = Load();
            settings.RepoInUseKey ??= onDisk.RepoInUseKey;
            settings.RemoteRepoSeen |= onDisk.RemoteRepoSeen || settings.RepoType != BlindRepoType.Folder;
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
    /// Read, change and save under the write lock; nothing is written when <paramref name="change"/> says
    /// no, and nothing is written over a file that cannot be read: a change nobody asked for must not
    /// replace what an operator could still repair by hand (it may hold the restic password).
    /// </summary>
    public bool Update(Func<BlindBackupSettings, bool> change)
    {
        lock (_write)
        {
            if (!TryLoad(out var s)) return false;
            if (!change(s)) return false;
            Save(s);
            return true;
        }
    }

    // Held by the first backup from "read the restic password" to "the repository exists and is marked in
    // use", so the password a repository is created with is the one the settings keep. A console password
    // change (which would move it) and a settings write that changes it do not wait: they skip / are refused.
    private readonly SemaphoreSlim _repoGate = new(1, 1);

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public async Task<IDisposable> EnterRepositoryAsync(CancellationToken ct)
    {
        await _repoGate.WaitAsync(ct);
        return new Lease(_repoGate);
    }

    /// <summary>Null when a backup is finding or creating the repository right now.</summary>
    public IDisposable? TryEnterRepository() => _repoGate.Wait(0) ? new Lease(_repoGate) : null;

    /// <summary>A backup found or created the repository <paramref name="repositoryKey"/>: from now on its password is fixed (see <see cref="BlindBackupSettings.RepoInUseKey"/>).</summary>
    public void MarkRepositoryInUse(string repositoryKey)
    {
        try
        {
            Update(s =>
            {
                if (s.RepoInUseKey == repositoryKey) return false;
                s.RepoInUseKey = repositoryKey;
                return true;
            });
        }
        catch (SettingsClosedException)
        {
            // The node is being wiped; the settings this would mark are going away.
        }
    }

    /// <summary>
    /// The console password is the default restic password. Called from the handler that has just
    /// stored a new console password, because that is the only moment the plaintext exists (the node
    /// keeps a hash of it). It follows the console password until the operator enters a restic
    /// password of their own or a repository exists: a repository is encrypted under the password it
    /// was created with, so changing the setting afterwards would lock the owner out of the backups.
    /// A password found without a recorded source (a file from before this field) counts as the
    /// operator's own. Returns whether the restic password was set.
    /// </summary>
    public bool AdoptConsolePassword(string consolePassword)
    {
        // A backup that is creating the repository right now keeps the password it read: the new one
        // must not land between its "read" and its "init" (it would orphan the repository).
        using var lease = TryEnterRepository();
        if (lease is null) return false;
        try
        {
            return Update(s =>
            {
                if (s.RepositoryExists()) return false;
                var followsConsole = s.ResticPasswordSource == ResticPasswordSources.Console;
                if (!followsConsole && !string.IsNullOrEmpty(s.ResticPassword)) return false;
                s.ResticPassword = consolePassword;
                s.ResticPasswordSource = ResticPasswordSources.Console;
                return true;
            });
        }
        catch (SettingsClosedException)
        {
            return false;
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
        // Not a secret: the form says whether the password follows the console password.
        ResticPasswordSource = string.IsNullOrEmpty(s.ResticPassword) ? null : s.ResticPasswordSource,
        RepoInUse = s.RepositoryInUse(),
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
