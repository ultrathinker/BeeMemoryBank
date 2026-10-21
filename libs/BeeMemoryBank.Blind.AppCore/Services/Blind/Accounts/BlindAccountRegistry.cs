using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What <see cref="BlindAccountRegistry.Load"/> found on disk.</summary>
public enum BlindRegistryState
{
    /// <summary>There is no <c>accounts.json</c> and nothing under <c>accounts/</c>: a fresh install, or a 2.6 install that is not adopted yet.</summary>
    Missing,

    /// <summary>The file was read and every rule holds.</summary>
    Loaded,

    /// <summary>The file cannot be read, is not what this version writes, or is missing while account folders exist. Nothing is written until a person has dealt with it.</summary>
    Damaged,
}

/// <summary>A change was asked of a registry that is damaged: it is never written over (that would "start empty over" the account folders it names).</summary>
public class BlindRegistryDamagedException(string message) : IOException(message), BeeMemoryBank.Sync.Blind.IBlindOwnSentence;

/// <summary>A change was refused because another copy of the app holds the data folder (see <see cref="BlindRootLock"/>): nothing was read or written.</summary>
public sealed class BlindRegistryBusyException(string message) : BlindRegistryDamagedException(message);

/// <summary>
/// <c>accounts.json</c> (version 1): the list of the accounts of this device and the device's own limits, in the app's root folder. Written whole to a
/// temporary file (a name of its own each time) and renamed into place, so a reader (or a crash) sees the old file or the new one and never half of either.
///
/// <para><b>Two kinds of lock.</b> A <c>lock</c> statement keeps the threads of this object apart. Between objects and processes the OS lock on the root
/// (<see cref="BlindRootLock"/>) decides: a host holds it for as long as it lives and hands it to its registry (<see cref="Hold"/>); a registry used alone takes it for
/// one update. Either way an update starts from what is on disk NOW, not from what this object read earlier, so two registries on one folder never lose an update.</para>
///
/// <para>Fail closed: a file that is damaged, newer than this version, breaks a rule (an id that is not 32 hex characters becomes a folder name, so
/// it is checked here before anything uses it), is a link, sits in a root or beside an <c>accounts</c> folder that is a link, or is missing while <c>accounts/</c> holds
/// folders leaves the registry <see cref="BlindRegistryState.Damaged"/>. It then offers nothing to start and refuses every change; the host opens on the sentence in
/// <see cref="Problem"/> and touches no account.</para>
/// </summary>
public sealed class BlindAccountRegistry
{
    public const string FileName = "accounts.json";
    public const string AccountsFolder = "accounts";
    public const int MaxAccountsInFile = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly IBlindCrashPoints _crash;
    private readonly TimeSpan _lockWait;
    private BlindRootLock? _held;
    private BlindAccountsFile _current = BlindAccountsFile.Empty;
    private BlindRegistryState _state = BlindRegistryState.Missing;
    private string? _problem;

    public BlindAccountRegistry(string rootDirectory, IBlindCrashPoints? crashPoints = null) : this(rootDirectory, crashPoints, DefaultLockWait)
    {
    }

    internal BlindAccountRegistry(string rootDirectory, IBlindCrashPoints? crashPoints, TimeSpan lockWait)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _crash = crashPoints ?? NoCrashPoints.Instance;
        _lockWait = lockWait;
    }

    /// <summary>The app's root folder: where <c>accounts.json</c> and <c>accounts/</c> are.</summary>
    public string RootDirectory { get; }

    public string FilePath => Path.Combine(RootDirectory, FileName);

    public string AccountsDirectory => Path.Combine(RootDirectory, AccountsFolder);

    public BlindRegistryState State { get { lock (_gate) return _state; } }

    /// <summary>The sentence that says what is wrong while <see cref="State"/> is <see cref="BlindRegistryState.Damaged"/>; otherwise null.</summary>
    public string? Problem { get { lock (_gate) return _problem; } }

    /// <summary>The accounts as last read or written. An empty file while Missing or Damaged (nothing may be started from it).</summary>
    public BlindAccountsFile Current { get { lock (_gate) return _current; } }

    /// <summary>The host owns the root for as long as it lives: this registry then takes no lock of its own per update.</summary>
    internal void Hold(BlindRootLock held)
    {
        lock (_gate) _held = held;
    }

    /// <summary>The host let go of the root.</summary>
    internal void Release()
    {
        lock (_gate) _held = null;
    }

    /// <summary>Reads the file again. Never throws for a bad file: it leaves the registry damaged and says why.</summary>
    public BlindRegistryState Load()
    {
        lock (_gate) return LoadCore();
    }

    private BlindRegistryState LoadCore()
    {
        try
        {
            // First: is the path what it should be. A link (or a dangling one) is judged before anything is read or created through it.
            if (BlindPathSafety.RegistryFileProblem(RootDirectory, FilePath) is { } fileProblem)
                return Damage(fileProblem + " Nothing was started or changed.");
            if (BlindPathSafety.AccountsFolderProblem(RootDirectory) is { } accountsProblem)
                return Damage(accountsProblem + " Nothing was started or changed.");

            if (!File.Exists(FilePath))
            {
                var orphans = CountOrphanEntries();
                if (orphans > 0)
                    return Damage($"{FileName} is missing, but the folder {AccountsFolder} holds {orphans} item(s). Nothing was started or changed, so that nothing is lost.");
                _current = BlindAccountsFile.Empty;
                _problem = null;
                return _state = BlindRegistryState.Missing;
            }

            var bytes = ReadAllBytesWithRetry(FilePath);
            BlindAccountsFile? file;
            try { file = JsonSerializer.Deserialize<BlindAccountsFile>(bytes, Json); }
            catch (JsonException) { return Damage($"{FileName} is damaged and cannot be read. Nothing was started or changed, so that nothing is lost."); }
            if (file is null) return Damage($"{FileName} is empty or damaged. Nothing was started or changed, so that nothing is lost.");
            if (file.Version != BlindAccountsFile.CurrentVersion)
                return Damage($"{FileName} was written by another version of this app (format {file.Version}). Nothing was started or changed.");
            if (Validate(file) is { } rule)
                return Damage($"{FileName} is not valid ({rule}). Nothing was started or changed, so that nothing is lost.");

            _current = file;
            _problem = null;
            return _state = BlindRegistryState.Loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Damage($"{FileName} could not be read ({ex.GetType().Name}). Nothing was started or changed; try again.");
        }
    }

    /// <summary>A writer in another process may be between its two steps; a few short tries before a read counts as failed.</summary>
    private static byte[] ReadAllBytesWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException and not FileNotFoundException and not DirectoryNotFoundException && attempt < 4) { Thread.Sleep(20); }
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the content on disk now and writes the result atomically. Returns what was written. Throws
    /// <see cref="BlindRegistryDamagedException"/> while the registry is damaged (or <see cref="BlindRegistryBusyException"/> when another copy holds the folder), and
    /// <see cref="InvalidOperationException"/> when the change itself breaks a rule of the file (a bug of the caller, never a state of the disk).
    /// </summary>
    public BlindAccountsFile Update(Func<BlindAccountsFile, BlindAccountsFile> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            if (_state == BlindRegistryState.Damaged) throw new BlindRegistryDamagedException(_problem ?? $"{FileName} is damaged.");

            // The root is ours for the length of the update: the host's lease, or one taken here.
            using var own = _held is null ? TakeLock() : null;

            // From what is on disk now: another registry may have written since this one last read.
            if (LoadCore() == BlindRegistryState.Damaged) throw new BlindRegistryDamagedException(_problem ?? $"{FileName} is damaged.");

            var next = change(_current) with { Version = BlindAccountsFile.CurrentVersion };
            if (Validate(next) is { } rule) throw new BeeMemoryBank.Sync.Blind.BlindSentenceException($"The change would make {FileName} invalid: {rule}");
            Write(next);
            _current = next;
            _problem = null;
            _state = BlindRegistryState.Loaded;
            return next;
        }
    }

    private BlindRootLock TakeLock() =>
        BlindRootLock.TryAcquire(RootDirectory, _lockWait, out var problem)
        ?? throw new BlindRegistryBusyException(problem ?? BlindRootLock.RefusedSentence);

    /// <summary>Null when every rule holds, otherwise the first rule that is broken, in words.</summary>
    public static string? Validate(BlindAccountsFile file)
    {
        if (file.Entries.Count > MaxAccountsInFile) return $"more than {MaxAccountsInFile} accounts";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.Entries)
        {
            if (entry is null) return "an empty entry";
            if (!BlindAccountEntry.IsValidId(entry.Id)) return "an account id that is not one this app makes";
            if (!ids.Add(entry.Id)) return "the same account id twice";
            var legacy = entry.Id == BlindAccountEntry.LegacyId;
            if (entry.Layout is not (BlindAccountLayouts.Root or BlindAccountLayouts.Dir)) return "an unknown folder layout";
            if (legacy != (entry.Layout == BlindAccountLayouts.Root)) return "an account id and a folder layout that do not belong together";
            if (BlindAccountEntry.CheckName(entry.Name) is not null) return "an account name that cannot be used";
            if (!BlindAccountPhases.All.Contains(entry.Phase)) return "an unknown account state";
            if (entry.SourceOrigin is { Length: > 256 } || entry.InviteId is { Length: > 64 }) return "an over-long field";
            if (entry.MatchCode is not null && BeeMemoryBank.Core.Models.BlindInviteMatchCode.Normalize(entry.MatchCode) != entry.MatchCode) return "a match code that is not one";
            if (entry.EffectiveLimits is { MaxStoredBytes: < 0 } or { MaxTrafficBytesPerDay: < 0 } or { MaxRequestsPerDay: < 0 }) return "a negative limit";
        }
        if (file.DeviceLimits is { MaxStoredBytes: < 0 } or { MaxTrafficBytesPerDay: < 0 }) return "a negative device limit";
        return null;
    }

    private BlindRegistryState Damage(string problem)
    {
        _current = BlindAccountsFile.Empty;
        _problem = problem;
        return _state = BlindRegistryState.Damaged;
    }

    /// <summary>Folders or files under <c>accounts/</c>: account data that a missing registry would hide.</summary>
    private int CountOrphanEntries()
    {
        var dir = AccountsDirectory;
        if (!Directory.Exists(dir)) return 0;
        return Directory.EnumerateFileSystemEntries(dir).Count();
    }

    private void Write(BlindAccountsFile file)
    {
        Directory.CreateDirectory(RootDirectory);
        _crash.Reach("registry:before-write");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);
        // A name of its own each time, created new (a link planted at a fixed name could not redirect it, and a leftover of a crash never gets in the way).
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _crash.Reach("registry:tmp-written");
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw;
        }
        _crash.Reach("registry:renamed");
        SweepLeftovers();
    }

    /// <summary>The temporary files an earlier writer left when it died between its two steps. Only ever called with the root locked: nobody else is writing one.</summary>
    private void SweepLeftovers()
    {
        try
        {
            foreach (var pattern in new[] { FileName + ".tmp", FileName + ".*.tmp" })
                foreach (var leftover in Directory.EnumerateFiles(RootDirectory, pattern))
                    TryDelete(leftover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover is only litter: the next update tries again.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
