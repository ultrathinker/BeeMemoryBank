using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.AppPaths;

/// <summary>What <see cref="SealIntent.Resolve"/> did at a start.</summary>
public enum SealIntentAction
{
    /// <summary>No intent was pending.</summary>
    None,
    /// <summary>The database is encrypted: the setup, join or restore had finished, and only the marker was left. It was removed.</summary>
    Completed,
    /// <summary>The setup, join or restore never reached the encrypted database: what it had written in the open was wiped, and the marker with it.</summary>
    Discarded,
    /// <summary>The start is refused by name (<see cref="SealStartRefusal"/>) and nothing was wiped that could not be proven to be the operation's: a file that would not
    /// go, a marker that cannot be trusted, files that are not what the operation wrote, a link, or an operation another process may still be running.</summary>
    Refused,
}

/// <param name="Action">What was done.</param>
/// <param name="Kind">What was interrupted (<c>setup</c>, <c>join</c>, <c>restore</c>), or null when no intent was pending.</param>
/// <param name="Wiped">The relative names that were wiped (empty unless <see cref="SealIntentAction.Discarded"/>).</param>
/// <param name="Refusal">Set when <paramref name="Action"/> is <see cref="SealIntentAction.Refused"/>.</param>
public sealed record SealIntentResolution(
    SealIntentAction Action, string? Kind = null, IReadOnlyList<string>? Wiped = null, SealStartRefusal? Refusal = null)
{
    public static readonly SealIntentResolution Nothing = new(SealIntentAction.None);

    public IReadOnlyList<string> WipedNames => Wiped ?? [];
}

/// <summary>A setup, join or restore cannot begin: another one is running or was cut off, the target is not an empty node, or a link is in the way. <see cref="Code"/> is one of the <c>SealIntent.*Code</c> values.</summary>
public sealed class SealIntentException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The durable "seal intent" of a node that is being set up (BMB-306): a marker, <c>&lt;data&gt;/seal-intent.json</c>, written BEFORE the first
/// thing a fresh setup, a join or a restore puts into the database in the open, and removed only when the node is encrypted (or was taken back to
/// empty). It is one mechanism for all of them: whatever kills the process in between - a power cut, a kill, a cleanup that throws - leaves the marker,
/// and the next start finds it before anything is opened (<see cref="VaultStartup.Enter"/>) and never serves a node that holds data in the open under it:
/// <list type="bullet">
/// <item>the database is already an encrypted pair: the setup had finished, the marker is removed;</item>
/// <item>otherwise what the interrupted flow wrote in the open - the vault database, the chat database, their sidecars, a half-made boot store,
///   the targets of a migration that never swapped, the working files - is wiped, and the marker with it. The node is the empty node it was before;
///   what the flow read from (the other computer, a backup, a blind copy) is untouched, so it is run again;</item>
/// <item>a file that cannot be wiped refuses the start by name; nothing is guessed.</item>
/// </list>
/// The marker holds no key, no password and no name of a note: the kind, a version, a time, the id of the operation, the id of the data folder and what
/// the folder held of the reserved names when the operation began. The password is never on disk, so a start cannot finish the encryption by itself; it can only decide that nothing
/// readable is left.
/// <para>
/// <b>A marker is never permission to erase on its own (BMB-306 fix round 2).</b> What is wiped must be proven to belong to the operation that wrote the marker;
/// in every other case the start is refused and NOTHING is touched, so that a stale, damaged or copied marker next to a node somebody keeps cannot cost them the node:
/// </para>
/// <list type="bullet">
/// <item>the marker must be one this version wrote (version 3, a kind, a time that can be right, an operation id) for an operation that began on an EMPTY target,
///   and for THIS folder: it names a folder id that the folder itself holds in <c>seal-intent.id</c>, so a marker copied alone into another folder (next to a node
///   somebody keeps) does not match, while a whole folder that is moved or copied after a cut carries both and is settled like the cut it is;</item>
/// <item>a database (the vault's or the chat's) is the operation's only if it is the very file the operation found when it began - the same volume and the same file index,
///   recorded in the marker - and the operation writes it in place. A write time is no proof: a node copied or restored into the folder later has a newer time than
///   any marker. The sidecars and the encryption's work files of a database follow it; what is not that file (a copy, a replacement, a file that appeared later) is
///   somebody's, and the start is refused with nothing touched;</item>
/// <item>nothing is followed through a link: a data folder, a database, the working folder or anything in it, and the marker, its folder id and their temp files, if they
///   are a symbolic link or a junction, refuse the start, because what is behind it cannot be proven to be inside the folder.</item>
/// </list>
/// <para>
/// One operation at a time, across processes: <see cref="Begin"/> takes an operation lease (<c>&lt;D&gt;.seal-intent.lease</c>, an exclusive OS lock that goes with
/// the process) and refuses while another process, or another operation of this one, holds it, or while a marker is pending. <see cref="Complete"/> releases it.
/// </para>
/// </summary>
public static class SealIntent
{
    public const string FileName = "seal-intent.json";

    /// <summary>The marker is pending and a file of the operation could not be wiped (<see cref="Resolve"/>), or a second operation tried to begin over a pending marker (<see cref="Begin"/>).</summary>
    public const string RefusedCode = "seal_intent_pending";
    public const string PendingCode = RefusedCode;

    /// <summary>The marker cannot be trusted (damaged, another version, from another folder, a time that cannot be right): nothing was touched.</summary>
    public const string UntrustedCode = "seal_intent_untrusted";

    /// <summary>A database is not the file the operation found when it began (a copy, a replacement, or one that appeared later): it is somebody's node, nothing was touched.</summary>
    public const string ForeignCode = "seal_intent_foreign";

    /// <summary>A link where it cannot be followed (the data folder, a database, the working folder or something in it): nothing was touched.</summary>
    public const string UnsafeCode = "seal_intent_unsafe";

    /// <summary>Another process (or another operation of this one) is running a setup, join or restore of this node.</summary>
    public const string BusyCode = "seal_intent_busy";

    /// <summary>The target is not an empty node: an operation never begins over one.</summary>
    public const string NotEmptyCode = "seal_intent_not_empty";

    public const string KindSetup = "setup";
    public const string KindJoin = "join";
    public const string KindRestore = "restore";

    private const int MarkerVersion = 3;

    /// <summary>How far in the future the time a marker names may be (a clock that moved).</summary>
    private static readonly TimeSpan MarkerClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>The folder id the marker names, kept in the folder itself: a marker copied alone into another folder finds none, or another.</summary>
    public const string FolderIdFileName = "seal-intent.id";

    private sealed record Marker(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("started_at")] string StartedAt,
        [property: JsonPropertyName("operation_id")] string? OperationId = null,
        [property: JsonPropertyName("folder")] string? Folder = null,
        [property: JsonPropertyName("empty_at_begin")] bool EmptyAtBegin = false,
        [property: JsonPropertyName("pre")] List<PreFile>? Pre = null);

    /// <summary>
    /// What the folder held of the operation's reserved names when it began: the length (-1 for a folder), the write time, and who the file is
    /// (<see cref="FileIdentity"/>: the volume and the file index; null when the platform cannot say, which proves nothing).
    /// </summary>
    private sealed record PreFile(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("length")] long Length,
        [property: JsonPropertyName("written")] long Written,
        [property: JsonPropertyName("volume")] ulong? Volume = null,
        [property: JsonPropertyName("index")] ulong? Index = null);

    /// <summary>The operation leases this process holds, by normalised data folder: one operation at a time, released by <see cref="Complete"/> or by the process's end.</summary>
    private static readonly ConcurrentDictionary<string, FileStream> Operations =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// The claim one flow (a setup, a join or a restore, from its request or command down) has on the operation it began. The flow enters it first thing;
    /// <see cref="Begin"/> records the operation's id in it, and the take-back of that flow asks <see cref="OwnsPending"/> - so a flow that fails before it began,
    /// or whose own <see cref="Begin"/> was refused, never takes back what another operation (of this process or of another) is writing.
    /// </summary>
    public sealed class Flow : IDisposable
    {
        private static readonly AsyncLocal<Flow?> Current = new();
        private readonly Flow? _outer;

        private Flow()
        {
            _outer = Current.Value;
            Current.Value = this;
        }

        internal string? OperationId { get; private set; }

        internal static Flow? Active => Current.Value;

        public static Flow Enter() => new();

        internal static void Began(string operationId)
        {
            if (Current.Value is { } flow) flow.OperationId = operationId;
        }

        public void Dispose() => Current.Value = _outer;
    }

    /// <summary>
    /// True when the pending marker is the operation the calling flow began (<see cref="Flow"/>). A caller outside any flow owns what this process holds
    /// (it began the operation, or nobody here did).
    /// </summary>
    public static bool OwnsPending(string dataDir)
    {
        var d = Path.GetFullPath(dataDir);
        if (Flow.Active is not { } flow) return Operations.ContainsKey(RekeySwapJournal.Normalize(d));
        return flow.OperationId is { } id && ReadMarker(d).Marker?.OperationId == id;
    }

    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    private static string IdPathFor(string dataDir) => Path.Combine(dataDir, FolderIdFileName);

    /// <summary>The operation lease, next to the data folder like the vault lease and the re-key lock.</summary>
    public static string OperationLeasePathFor(string dataDir) => RekeySwapJournal.Normalize(dataDir) + ".seal-intent.lease";

    /// <summary>True when a marker is there, a link in its place included (a dangling one too: that is no "nothing pending", it is refused by name when the start resolves).</summary>
    public static bool IsPending(string dataDir) => LinkEntry.Exists(PathFor(dataDir));

    /// <summary>The kind the marker names, <c>"unknown"</c> for one that cannot be read, or null when none is pending.</summary>
    public static string? KindOf(string dataDir)
    {
        var path = PathFor(dataDir);
        // The gate first: a data folder or a reserved name that is a link is not read through, not even for the kind.
        if (LinkGate.Linked(dataDir).Count > 0) return IsPending(dataDir) ? "unknown" : null;
        if (!IsPending(dataDir)) return null;
        try
        {
            var kind = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path))?.Kind;
            return string.IsNullOrWhiteSpace(kind) || kind.Length > 32 ? "unknown" : kind;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Takes the operation lease, checks that nothing stands in the way, writes the marker durably (temp file, flush, rename, directory flush) and returns only
    /// when it is on disk. Throws <see cref="SealIntentException"/> when another operation holds the lease or a marker is already pending (a second setup over a
    /// first one, or over one that was cut off and not yet resolved by a start), when the target is not an empty node, or when a link is in the way; throws
    /// the I/O exception when the marker cannot be written. The flow must not start then (it fails closed, before it has written anything).
    /// </summary>
    /// <param name="targetIsEmpty">The caller's check that the target holds no node (no identity, no key slot); asked after the lease is held. Null: the caller has
    /// verified it itself just before, and says so by the marker's <c>empty_at_begin</c>.</param>
    public static void Begin(string dataDir, string kind, Func<bool>? targetIsEmpty = null)
    {
        if (kind is not (KindSetup or KindJoin or KindRestore))
            throw new ArgumentException($"Unknown kind of seal intent: {kind}", nameof(kind));
        var d = Path.GetFullPath(dataDir);
        // The link gate FIRST (the data folder itself and every reserved name in it, live or dangling), before the folder is created or anything in it is asked about.
        if (LinkGate.Refusal(d) is { } gate) throw new SealIntentException(gate.Code, gate.Message);
        Directory.CreateDirectory(d);
        if (LinksAmongArtifacts(d) is { Count: > 0 } linked)   // what is below the working folder
            throw new SealIntentException(UnsafeCode, LinkMessage(linked));

        var key = RekeySwapJournal.Normalize(d);
        var lease = AcquireOperation(key);
        var written = false;
        try
        {
            if (IsPending(d))
                throw new SealIntentException(PendingCode,
                    "A setup, join or restore of this node was cut off and has not been settled yet: the next start of the node settles it. " +
                    "Another one cannot begin over it.");
            if (targetIsEmpty is not null && !targetIsEmpty())
                throw new SealIntentException(NotEmptyCode, "This folder already holds a node (an identity or a key slot); a setup, join or restore never begins over one.");

            // The folder id first, then the marker that names it: a cut between the two leaves an id and no marker, which is nothing pending.
            var folderId = Guid.NewGuid().ToString("N");
            WriteDurably(IdPathFor(d), fs => fs.Write(Encoding.ASCII.GetBytes(folderId)));
            var marker = new Marker(MarkerVersion, kind, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"),
                folderId, EmptyAtBegin: true, PreState(d));
            WriteDurably(PathFor(d), fs => JsonSerializer.Serialize(fs, marker));
            written = true;
            Flow.Began(marker.OperationId!);
        }
        finally
        {
            if (!written) ReleaseOperation(key, lease);
        }
    }

    /// <summary>
    /// Removes the marker (the node is encrypted, or was taken back to empty) and releases the operation lease. Returns false when the marker could not be
    /// removed: it then stays, and the next start decides from the files (an encrypted pair only loses the marker; anything else that is provably the
    /// operation's is wiped), so a stale marker never costs a finished node.
    /// </summary>
    public static bool Complete(string dataDir)
    {
        var key = RekeySwapJournal.Normalize(dataDir);
        try
        {
            File.Delete(PathFor(dataDir));
            File.Delete(PathFor(dataDir) + ".tmp");
            File.Delete(IdPathFor(dataDir));
            File.Delete(IdPathFor(dataDir) + ".tmp");
            DurableFs.FlushDirectory(dataDir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (Operations.TryGetValue(key, out var lease)) ReleaseOperation(key, lease);
        }
    }

    /// <summary>
    /// The refusal a start reports when it joined a process that already holds the vault and a marker is pending: another live process may be running the
    /// operation, so nothing is resolved, wiped or served from here. Null when no marker is pending.
    /// </summary>
    public static SealStartRefusal? BusyRefusal(string dataDir) =>
        IsPending(dataDir)
            ? new SealStartRefusal(BusyCode, [FileName],
                "A setup, join or restore of this node is pending (" + FileName + ") and another process of this node is running. It may be the one doing it, so nothing was " +
                "opened, moved or deleted, and this process does not start. Wait until it has finished and start again; if it was cut off, stop every process that uses " +
                "this data folder first: the next start then settles it.")
            : null;

    /// <summary>
    /// The decision of a start (see the type's summary). Called by <see cref="VaultStartup.Enter"/> with the exclusive lease held, right after the
    /// swap resolver, so a swap an interrupted encryption had begun is already finished (the pair is encrypted) or refused by name.
    /// </summary>
    /// <param name="wipe">Test seam: wipes one absolute path and says whether it is gone. Null: <see cref="SecureFileWipe"/>.</param>
    public static SealIntentResolution Resolve(string dataDir, Func<string, bool>? wipe = null)
    {
        var d = Path.GetFullPath(dataDir);

        // The link gate FIRST, before "does the folder exist", "is a marker pending" or any read: a data folder that is a link (what is behind it cannot be proven to be this
        // node's folder) or a reserved name in it that is one (a marker, a database, a sidecar, the boot store, a temp file...) is refused by name, live or dangling, whether or not
        // anything is pending, and nothing is read through it, not even the kind of the marker.
        if (LinkGate.Refusal(d) is { } gate) return Refused("unknown", gate);
        if (!Directory.Exists(d) || !IsPending(d)) return SealIntentResolution.Nothing;
        var kind = KindOf(d);

        // The encryption went through: nothing is in the open, the marker only waited for its removal (it is removed whatever it says: nothing is wiped).
        if (VaultStoreProbe.IsSealedPair(d))
        {
            Complete(d);
            return new SealIntentResolution(SealIntentAction.Completed, kind);
        }

        // A journal or a leftover of an encryption that the swap resolver could not place is the resolver's refusal to report; this does not guess
        // over it, and the marker stays until the resolver's case is settled.
        if (LinkEntry.Exists(SealJournal.PathFor(d)) || VaultStoreProbe.HasSealOldLeftovers(d))
            return SealIntentResolution.Nothing;

        // Is this marker the one a setup of THIS folder wrote? If not it is no permission to wipe anything.
        var (marker, why) = ReadMarker(d);
        if (marker is null)
            return Refused(kind, Untrusted(why!));
        if (TrustProblem(d, marker) is { } doubt)
            return Refused(kind, Untrusted(doubt));

        // What is there, and is it the operation's? All of it is decided before anything is wiped: one doubt wipes nothing.
        var links = LinksAmongArtifacts(d);
        if (links.Count > 0)
            return Refused(kind, new SealStartRefusal(UnsafeCode, links, LinkMessage(links)));
        // The two databases are what a node is: each is the operation's only if it is the file the operation found when it began. Everything else it can have
        // written (sidecars, the encryption's work files, a half-made boot store, the working folder) follows from them and is wiped with them; with one database
        // that cannot be proven the operation's, nothing is.
        var foreign = new List<string>();
        foreach (var name in new[] { VaultStoreProbe.Database, VaultStoreProbe.Chat })
        {
            var path = Path.Combine(d, name);
            if (File.Exists(path) && !FoundByTheOperation(path, name, marker)) foreign.Add(name);
            else if (Directory.Exists(path)) foreign.Add(name);
        }
        if (foreign.Count > 0)
            return Refused(kind, new SealStartRefusal(ForeignCode, foreign,
                "A setup, join or restore of this node was interrupted, but " + string.Join(", ", foreign) + (foreign.Count == 1 ? " is" : " are") +
                " not the " + (foreign.Count == 1 ? "file" : "files") + " it found when it began (a copy, a replacement, or " + (foreign.Count == 1 ? "a file" : "files") +
                " that appeared later), so " + (foreign.Count == 1 ? "it does" : "they do") +
                " not look like what it wrote: this may be a node you keep. Nothing was opened, moved or deleted. If the folder holds a node you want to keep, remove only " +
                FileName + " and start again; if it holds nothing you need, remove those files and " + FileName + " and start again."));
        var toWipe = new List<string>();
        foreach (var name in Artifacts(d))
            if (File.Exists(Path.Combine(d, name)) || Directory.Exists(Path.Combine(d, name))) toWipe.Add(name);

        var wiped = new List<string>();
        var stuck = new List<string>();
        foreach (var name in toWipe)
        {
            var path = Path.Combine(d, name);
            if (Directory.Exists(path))
            {
                WipeFolder(d, path, wipe, wiped, stuck);
                continue;
            }
            if (wipe?.Invoke(path) ?? SecureFileWipe.TryWipe(path)) wiped.Add(name);
            else stuck.Add(name);
        }
        if (stuck.Count > 0)
            return new SealIntentResolution(SealIntentAction.Refused, kind, wiped, Refusal(stuck));

        FlushQuietly(d);
        if (!Complete(d))
            return new SealIntentResolution(SealIntentAction.Refused, kind, wiped, Refusal([FileName]));
        return new SealIntentResolution(SealIntentAction.Discarded, kind, wiped);
    }

    // ---- the operation lease -------------------------------------------------------------------------------------------------------------

    private static FileStream AcquireOperation(string key)
    {
        var path = key + ".seal-intent.lease";
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            if (Operations.TryAdd(key, stream)) return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // held by another process, or by another operation of this one
        }
        stream?.Dispose();
        throw new SealIntentException(BusyCode,
            "Another setup, join or restore of this node is running (this process or another one holds " + Path.GetFileName(path) + "). Only one can run at a time; " +
            "wait until it has finished.");
    }

    private static void ReleaseOperation(string key, FileStream lease)
    {
        Operations.TryRemove(new KeyValuePair<string, FileStream>(key, lease));
        lease.Dispose();
    }

    // ---- what the marker proves ----------------------------------------------------------------------------------------------------------

    private static (Marker? Marker, string? Why) ReadMarker(string d)
    {
        Marker? marker;
        if (LinkEntry.IsLink(PathFor(d))) return (null, "it is a link");
        try
        {
            marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(PathFor(d)));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return (null, "it cannot be read");
        }
        if (marker is null) return (null, "it is empty");
        if (marker.Version != MarkerVersion) return (null, "it is of another version");
        if (marker.Kind is not (KindSetup or KindJoin or KindRestore)) return (null, "it names no kind of operation this program runs");
        if (!Guid.TryParseExact(marker.OperationId, "N", out _)) return (null, "it names no operation");
        if (string.IsNullOrEmpty(marker.Folder) || marker.Pre is null) return (null, "it does not say which folder it belongs to");
        if (!marker.EmptyAtBegin) return (null, "it does not say that the node was empty when the operation began");
        if (!DateTime.TryParse(marker.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return (null, "it carries no time");
        return (marker, null);
    }

    /// <summary>Why a well-formed marker is still not the one this folder's operation wrote, or null when nothing speaks against it.</summary>
    private static string? TrustProblem(string d, Marker marker)
    {
        string? held = null;
        try { held = File.Exists(IdPathFor(d)) && !LinkEntry.IsLink(IdPathFor(d)) ? File.ReadAllText(IdPathFor(d)).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!string.Equals(marker.Folder, held, StringComparison.Ordinal))
            return "it was written for another folder (it was copied here without the folder it belongs to)";
        var startedAt = DateTime.Parse(marker.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        if (startedAt > DateTime.UtcNow + MarkerClockSkew) return "it carries a time in the future";
        return null;
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> is the one the operation found under <paramref name="name"/> when it began: the same volume and the same file
    /// index as the marker recorded. The operation writes it in place, so its identity does not change; a node copied or restored into the folder afterwards is another
    /// file, whatever its write time. A file the operation did not find (it was not there), one whose identity was not recorded, or one whose identity cannot be read now
    /// is not provably the operation's.
    /// </summary>
    private static bool FoundByTheOperation(string path, string name, Marker marker)
    {
        var found = marker.Pre!.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (found?.Volume is not { } volume || found.Index is not { } index) return false;
        return FileIdentity.TryGet(path) is { } now && now.Volume == volume && now.Index == index;
    }

    private static List<PreFile> PreState(string d)
    {
        var pre = new List<PreFile>();
        foreach (var name in Artifacts(d))
        {
            var path = Path.Combine(d, name);
            var identity = FileIdentity.TryGet(path);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                pre.Add(new PreFile(name, info.Length, info.LastWriteTimeUtc.Ticks, identity?.Volume, identity?.Index));
            }
            else if (Directory.Exists(path))
                pre.Add(new PreFile(name, -1, Directory.GetLastWriteTimeUtc(path).Ticks, identity?.Volume, identity?.Index));
        }
        return pre;
    }

    /// <summary>Writes a small file durably: temp file, flush to disk, rename over it, directory flush.</summary>
    private static void WriteDurably(string path, Action<FileStream> write)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            write(fs);
            fs.Flush(flushToDisk: true);
        }
        DurableFs.MoveFile(tmp, path);
    }

    // ---- links (a link is judged by the entry itself, see LinkEntry: a link to nothing is one too) -------------------------------------------

    /// <summary>The working folder: the downloaded archive and the extracted copy of a setup, join or restore.</summary>
    internal const string WorkingFolder = "tmp";

    /// <summary>The marker, the folder id and the temp files they are written through: what is read (or written) before anything else is decided.</summary>
    internal static IEnumerable<string> MarkerFiles()
    {
        foreach (var name in new[] { FileName, FolderIdFileName })
        {
            yield return name;
            yield return name + ".tmp";
        }
    }

    /// <summary>The reserved names (<see cref="LinkGate"/>) and the entries below the working folder that are links, relative to the folder. Nothing is followed to find them.</summary>
    private static List<string> LinksAmongArtifacts(string d)
    {
        var links = LinkGate.Linked(d);
        if (LinkEntry.IsLink(RekeySwapJournal.Normalize(d))) return links;   // the folder itself is a link: nothing inside is looked at
        var working = Path.Combine(d, WorkingFolder);
        if (!links.Contains(WorkingFolder) && Directory.Exists(working)) links.AddRange(LinksBelow(d, working));
        return links;
    }

    private static IEnumerable<string> LinksBelow(string d, string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (LinkEntry.IsLink(entry)) { yield return Path.GetRelativePath(d, entry); continue; }
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    // ---- the wipe -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a setup, join or restore can have written in the open, relative to the data folder: the two databases and their sidecars, the targets of
    /// a migration, a boot store that is ours, and the working folder (<c>tmp</c>: the downloaded archive and the extracted copy).
    /// </summary>
    internal static IEnumerable<string> Artifacts(string d)
    {
        foreach (var db in new[] { VaultStoreProbe.Database, VaultStoreProbe.Chat })
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", VaultStoreProbe.SealNewSuffix, VaultStoreProbe.SealNewSuffix + "-wal",
                         VaultStoreProbe.SealNewSuffix + "-shm", VaultStoreProbe.SealNewSuffix + "-journal" })
                yield return db + suffix;

        // The boot store is ours when it says so; a file by that name that is not is somebody else's and is left to the start's own refusal.
        var boot = Path.Combine(d, VaultStoreProbe.Boot);
        if (LinkEntry.IsLink(boot) || (File.Exists(boot) && VaultStoreProbe.IsBootStore(boot)))
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                yield return VaultStoreProbe.Boot + suffix;
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            yield return VaultStoreProbe.Boot + VaultStoreProbe.SealNewSuffix + suffix;

        yield return WorkingFolder;
    }

    /// <summary>Wipes the files below <paramref name="folder"/> and then the folders, never following a link (one found now stops the wipe of the folder: it is stuck).</summary>
    private static void WipeFolder(string d, string folder, Func<string, bool>? wipe, List<string> wiped, List<string> stuck)
    {
        var files = new List<string>();
        var folders = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (LinkEntry.IsLink(entry))
                {
                    stuck.Add(Path.GetRelativePath(d, entry));
                    return;
                }
                if (Directory.Exists(entry)) { folders.Add(entry); pending.Push(entry); }
                else files.Add(entry);
            }
        }

        var ok = true;
        foreach (var file in files)
        {
            if (wipe?.Invoke(file) ?? SecureFileWipe.TryWipe(file)) continue;
            ok = false;
            stuck.Add(Path.GetRelativePath(d, file));
        }
        if (!ok) return;
        try
        {
            foreach (var child in folders.OrderByDescending(p => p.Length))
                Directory.Delete(child, recursive: false);
            Directory.Delete(folder, recursive: false);
            wiped.Add(Path.GetRelativePath(d, folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stuck.Add(Path.GetRelativePath(d, folder));
        }
    }

    // ---- what a person is told ---------------------------------------------------------------------------------------------------------

    private static SealIntentResolution Refused(string? kind, SealStartRefusal refusal) =>
        new(SealIntentAction.Refused, kind, null, refusal);

    private static string LinkMessage(IReadOnlyList<string> links) =>
        "A setup, join or restore of this node was interrupted, or is about to begin, and " + string.Join(", ", links) + (links.Count == 1 ? " is a link" : " are links") +
        " (a symbolic link or a junction): a link is never followed or removed here, because what is behind it cannot be proven to be inside the data folder. " +
        "Nothing was opened, moved or deleted. Remove the link yourself (not what it points to), and start again.";

    private static SealStartRefusal Untrusted(string why) => new(
        UntrustedCode, [FileName],
        "The setup marker of this node (" + FileName + ") cannot be trusted: " + why + ". It is not taken as permission to delete anything, so nothing was opened, moved " +
        "or deleted. If this folder holds a node you want to keep, remove only " + FileName + " and start again. If an interrupted setup, join or restore left it and the " +
        "folder holds nothing you need, remove " + FileName + ", the databases and the tmp folder and start again.");

    private static SealStartRefusal Refusal(IReadOnlyList<string> files) => new(
        RefusedCode, files,
        "A setup, join or restore of this node was interrupted, and what it had written in the open could not be removed: " + string.Join(", ", files)
        + ". Nothing else was opened, moved or deleted. Stop everything that uses this data folder, remove those files, and start again; "
        + "the node is then empty and the setup is run again (what it read from - the other computer, the backup, the blind copy - is untouched).");

    private static void FlushQuietly(string d)
    {
        try { DurableFs.FlushDirectory(d); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
