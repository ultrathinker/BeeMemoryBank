using System.Runtime.InteropServices;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// A job that has to hold a whole database in memory (the BMBDB1/2 container of a snapshot is one AES-GCM message, so its image is one buffer) cannot be
/// refused by an <see cref="OutOfMemoryException"/> halfway, in a container that is killed or a process that fails with a type name nobody can act on
/// (BMB-304: a 645 MB database in a 3 GB container). It asks first, and says what is missing: how much it needs, how much is free, and the limit that
/// would be enough.
/// </summary>
public sealed class NotEnoughMemoryException(string message, long neededBytes, long freeBytes, long recommendedLimitBytes) : InvalidOperationException(message)
{
    /// <summary>What the job needs on top of what the process holds.</summary>
    public long NeededBytes { get; } = neededBytes;

    /// <summary>What the process may still take.</summary>
    public long FreeBytes { get; } = freeBytes;

    /// <summary>The memory limit of the node (for a container: its memory limit) that would be enough, rounded up to 256 MB.</summary>
    public long RecommendedLimitBytes { get; } = recommendedLimitBytes;
}

/// <summary>
/// What the platform and the runtime say about the memory this process lives in, at one moment (<c>MemoryBudget.Read</c>). The numbers of the model
/// that answers "does it fit" and "which limit would be enough" (<see cref="MemoryBudget.Free"/>, <see cref="MemoryBudget.RecommendedLimit"/>), so that the
/// two answers can not disagree.
/// </summary>
/// <param name="ProcessLimitBytes">The hard limit of the whole process: the cgroup limit of a container, the job object limit on Windows, else the memory of the machine.</param>
/// <param name="HeapLimitBytes">The ceiling of the managed heap the runtime enforces: in a container or a job a fraction of the process limit (75% by default),
/// not the limit itself. A managed array that does not fit under it is an <see cref="OutOfMemoryException"/> whatever the container has left.</param>
/// <param name="ManagedBytes">Managed memory that is alive after a full collection.</param>
/// <param name="NativeBytes">Resident memory that is not the managed heap: SQLite caches, the embedding model, the runtime itself.</param>
/// <param name="LimitIsOperatorSet">The process limit is a container's or a job's, which the operator can raise (not the memory of the machine).</param>
/// <param name="HeapLimitIsFixed">The heap limit is configured as a number (<c>DOTNET_GCHeapHardLimit</c>): raising the container does not raise it.</param>
internal readonly record struct MemoryReading(
    long ProcessLimitBytes, long HeapLimitBytes, long ManagedBytes, long NativeBytes, bool LimitIsOperatorSet, bool HeapLimitIsFixed);

/// <summary>
/// The ONE owner of "does this job fit in memory" for every allocation that holds a whole database image: the managed buffer of a container
/// (<see cref="Allocate"/>, <see cref="Bmbdb2Container"/>) and the native copy the SQLite importer needs for an image with no page reserve
/// (<see cref="AllocateNative"/>, <see cref="PlainImageImporter"/>). It asks first and refuses with the words of <see cref="NotEnoughMemoryException"/>;
/// what slips through is caught where the buffer is taken.
///
/// <para>The model (BMB-304): a managed buffer must fit under the heap limit the runtime enforces AND, with what the process already holds natively,
/// under the limit of the process (a container's memory limit, a Windows job limit, the machine). The first is a fraction of the second in a container,
/// so a container of 1 GiB gives a managed array of at most about 768 MiB minus what is alive: the recommended limit is computed from the same two
/// conditions, never from the size of the job alone. A host with no limit is bounded by its memory; headroom of other processes is not counted (a
/// refusal that is not true stops a rotation or a restore that would have worked).</para>
/// </summary>
public static class MemoryBudget
{
    private const long Megabyte = 1024 * 1024;

    /// <summary>The room a job needs besides its buffer: the runtime's own growth, a second page cache, the HTTP request that asked for it.</summary>
    public const long Slack = 64 * Megabyte;

    /// <summary>Test seam: the reading the model works from, in place of the platform's (scoped to the test's own async flow).</summary>
    internal static readonly AsyncLocal<MemoryReading?> ReadingForTests = new();

    /// <summary>Test seam: every whole-image allocation made through this type, in order, while a test has a list here.</summary>
    internal static readonly AsyncLocal<List<(string What, long Bytes, bool Native)>?> Allocations = new();

    /// <summary>The platform's and the runtime's numbers now (a forced collection is part of it: the managed number is the memory that is alive).</summary>
    internal static MemoryReading Read()
    {
        if (ReadingForTests.Value is { } fixedReading) return fixedReading;

        var gc = GC.GetGCMemoryInfo();
        var managed = GC.GetTotalMemory(forceFullCollection: true);
        // What the limit is charged for is the cgroup's usage (the siblings of this process in the container, tmpfs, mapped files, the kernel's share), not this process's
        // resident set: the larger of the two is what is taken, the committed managed heap is the part of it that is not native.
        var used = Math.Max(Environment.WorkingSet, MemoryLimits.ProcessUsage() ?? 0);
        var native = Math.Max(0, used - gc.TotalCommittedBytes);
        var runtimeLimit = Math.Max(1, gc.TotalAvailableMemoryBytes);   // the memory of the machine, or the heap limit the runtime was given

        var processLimit = MemoryLimits.ProcessLimit();
        var operatorSet = processLimit is not null;
        var limit = processLimit ?? runtimeLimit;
        // The heap can never be larger than the process; when the platform's limit could not be read, the runtime's own number stands for both.
        var heapLimit = Math.Min(runtimeLimit, limit);
        return new MemoryReading(limit, heapLimit, managed, native, operatorSet, MemoryLimits.HeapLimitIsConfigured());
    }

    /// <summary>
    /// How much more a job may take, of a managed buffer or (<paramref name="native"/>) of native memory: the room under the heap limit and under the
    /// process limit, the smaller of the two. Zero when there is none.
    /// </summary>
    internal static long Free(MemoryReading reading, bool native = false)
    {
        var processFree = reading.ProcessLimitBytes - reading.NativeBytes - reading.ManagedBytes;
        return Math.Max(0, native ? processFree : Math.Min(processFree, reading.HeapLimitBytes - reading.ManagedBytes));
    }

    /// <summary>What the process may still take as one managed buffer, by the model of <see cref="Free"/>.</summary>
    public static long FreeBytes() => Free(Read());

    /// <summary>
    /// The process limit (a container's memory limit) that would be enough for a job that needs <paramref name="neededBytes"/> of managed memory and
    /// <paramref name="nativeBytes"/> of native memory besides, rounded up to 256 MB: the larger of what the process limit and what the heap limit need,
    /// the heap limit being the fraction of the process limit the reading shows.
    /// </summary>
    internal static long RecommendedLimit(MemoryReading reading, long neededBytes, long nativeBytes = 0)
    {
        var processNeed = reading.NativeBytes + reading.ManagedBytes + neededBytes + nativeBytes;
        var heapNeed = reading.ManagedBytes + neededBytes;
        if (neededBytes > 0 && reading.ProcessLimitBytes > 0 && reading.HeapLimitBytes < reading.ProcessLimitBytes && !reading.HeapLimitIsFixed)
            heapNeed = (long)Math.Ceiling(heapNeed * ((double)reading.ProcessLimitBytes / reading.HeapLimitBytes));
        else if (reading.HeapLimitIsFixed)
            heapNeed = 0;   // the container limit does not move it: the refusal says so
        return RoundUp(Math.Max(processNeed, heapNeed), 256 * Megabyte);
    }

    /// <summary>
    /// Refuses, before anything is read or allocated, a job that needs <paramref name="neededBytes"/> more than the process may take.
    /// </summary>
    /// <param name="what">The job, as the sentence starts: "Making the snapshot of a 645 MB database".</param>
    /// <param name="freeBytesOverride">The room to measure against instead of asking the platform: a test's ceiling.</param>
    /// <param name="nativeBytes">Native memory the job needs besides the managed buffer (an importer's copy of the image).</param>
    /// <exception cref="NotEnoughMemoryException">The room is not there; the message names the limit that would be enough.</exception>
    public static void RequireFree(long neededBytes, string what, long? freeBytesOverride = null, long nativeBytes = 0)
    {
        var reading = Read();
        var free = freeBytesOverride ?? Free(reading);
        if (neededBytes + nativeBytes <= free) return;
        throw Refusal(reading, neededBytes, nativeBytes, what, free);
    }

    /// <summary>
    /// Takes the one managed buffer a job needs. The estimate of <see cref="RequireFree"/> errs on the side of "enough"; when the runtime then cannot give the
    /// buffer (an <see cref="OutOfMemoryException"/>: the memory limit of the node is below what it needs) the answer is the same words, not an exception type.
    /// </summary>
    public static byte[] Allocate(long bytes, string what)
    {
        Allocations.Value?.Add((what, bytes, false));
        try
        {
            return new byte[bytes];
        }
        catch (OutOfMemoryException)
        {
            var reading = Read();
            throw Refusal(reading, bytes + Slack, 0, what, Free(reading));
        }
    }

    /// <summary>
    /// Takes native memory for a job that has to hold a whole image outside the managed heap (the SQLite importer's copy of an image with no page reserve):
    /// the room under the process limit is asked for first, and a failure of the allocation itself is the same refusal. The caller frees it with
    /// <see cref="Marshal.FreeHGlobal"/>.
    /// </summary>
    internal static nint AllocateNative(long bytes, string what, long? freeBytesOverride = null)
    {
        var reading = Read();
        var free = freeBytesOverride ?? Free(reading, native: true);
        if (bytes + Slack > free) throw Refusal(reading, bytes + Slack, 0, what, free, native: true);
        Allocations.Value?.Add((what, bytes, true));
        try
        {
            return Marshal.AllocHGlobal(checked((nint)bytes));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException)
        {
            throw Refusal(reading, bytes + Slack, 0, what, Free(reading, native: true), native: true);
        }
    }

    private static NotEnoughMemoryException Refusal(MemoryReading reading, long neededBytes, long nativeBytes, string what, long free, bool native = false)
    {
        var recommended = native
            ? RoundUp(reading.NativeBytes + reading.ManagedBytes + neededBytes, 256 * Megabyte)
            : RecommendedLimit(reading, neededBytes, nativeBytes);
        var advice = reading.HeapLimitIsFixed && !native
            ? $"The heap of this node is limited by configuration (DOTNET_GCHeapHardLimit, System.GC.HeapHardLimit): raise it to at least {Mb(reading.ManagedBytes + neededBytes)} MB, " +
              $"and the memory limit of the node (for a container: its memory limit) to at least {Mb(recommended)} MB, and try again."
            : $"Raise the memory limit of the node (for a container: its memory limit) to at least {Mb(recommended)} MB and try again." +
              (reading.LimitIsOperatorSet ? string.Empty : " (This node has no limit of its own: that is the memory of the machine.)");
        return new NotEnoughMemoryException(
            $"{what} needs about {Mb(neededBytes + nativeBytes)} MB of free memory in one piece, and this node has {Mb(free)} MB free. {advice} Nothing was changed.",
            neededBytes + nativeBytes, free, recommended);
    }

    private static long RoundUp(long value, long step) => (value + step - 1) / step * step;

    private static long Mb(long bytes) => Math.Max(1, (bytes + Megabyte - 1) / Megabyte);
}

/// <summary>
/// The limits the platform puts on this process, read from the platform itself and not from the runtime's idea of them: a container's cgroup (Linux,
/// Android), a Windows job object. Null when nothing limits the process below the machine. The parsing is pure and takes the file reader, so the cases
/// (cgroup v1, v2, nested, unlimited) are tested with the text of real files.
/// </summary>
internal static class MemoryLimits
{
    /// <summary>The limit that applies to this process, or null when none does (or the platform cannot say).</summary>
    internal static long? ProcessLimit()
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
                return CgroupLimit(path => File.Exists(path) ? File.ReadAllText(path) : null);
            if (OperatingSystem.IsWindows())
                return WindowsJobLimit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException)
        {
            // A platform that will not say is a platform with no limit we know of; the runtime's own number still bounds the heap.
        }
        return null;
    }

    /// <summary>Test seam: the usage the platform reports, in place of the cgroup's (scoped to the test's own async flow).</summary>
    internal static readonly AsyncLocal<long?> UsageForTests = new();

    /// <summary>What the limiting cgroup of this process holds now (see <see cref="CgroupUsage"/>), or null when no cgroup limits it (or the platform cannot say).</summary>
    internal static long? ProcessUsage()
    {
        if (UsageForTests.Value is { } fixedUsage) return fixedUsage;
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
                return CgroupUsage(path => File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A platform that will not say is a platform whose usage we do not know; the resident set of the process still counts.
        }
        return null;
    }

    /// <summary>True when the managed heap limit is configured as a number: raising a container's limit then does not raise it.</summary>
    internal static bool HeapLimitIsConfigured() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPlus_GCHeapHardLimit"))
        || AppContext.GetData("System.GC.HeapHardLimit") is not null
        || AppContext.GetData("GCHeapHardLimit") is not null;

    /// <summary>
    /// The smallest finite memory limit of the cgroup this process is in and of its ancestors (cgroup v2: <c>memory.max</c>; v1: <c>memory.limit_in_bytes</c>
    /// of the memory controller), or null. <paramref name="read"/> returns the text of a file, or null when there is none.
    /// </summary>
    internal static long? CgroupLimit(Func<string, string?> read)
    {
        long? best = null;
        var (v2Path, v1Path) = CgroupPaths(read);

        foreach (var dir in Ancestors(v2Path))
            best = Smaller(best, ParseLimit(read($"/sys/fs/cgroup{dir}/memory.max")));
        if (v1Path is not null)
            foreach (var dir in Ancestors(v1Path))
                best = Smaller(best, ParseLimit(read($"/sys/fs/cgroup/memory{dir}/memory.limit_in_bytes")));
        else
            best = Smaller(best, ParseLimit(read("/sys/fs/cgroup/memory/memory.limit_in_bytes")));
        return best;
    }

    /// <summary>The cgroup of this process: the unified (v2) path, and the memory controller's path of a v1 hierarchy (null when there is none).</summary>
    private static (string V2Path, string? V1Path) CgroupPaths(Func<string, string?> read)
    {
        var v2Path = "/";
        string? v1Path = null;
        foreach (var line in (read("/proc/self/cgroup") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(':', 3);
            if (parts.Length < 3) continue;
            if (parts[0] == "0" && parts[1].Length == 0) v2Path = parts[2];
            else if (parts[1].Split(',').Contains("memory")) v1Path = parts[2];
        }
        return (v2Path, v1Path);
    }

    /// <summary>
    /// What the cgroup that LIMITS this process holds now: the cgroup of the chain with the smallest finite limit (the one that kills a process that goes past it),
    /// its usage (cgroup v2: <c>memory.current</c>; v1: <c>memory.usage_in_bytes</c>) less the inactive file cache, which the kernel gives back before it kills anything
    /// (the "working set" of a container). Null when no cgroup in the chain has a limit, or the usage cannot be read. The resident set of ONE process misses the
    /// siblings in the same container (the Node, the Api and the Web are three processes), tmpfs and the kernel's share; the cgroup charges all of them.
    /// </summary>
    internal static long? CgroupUsage(Func<string, string?> read)
    {
        var (v2Path, v1Path) = CgroupPaths(read);
        long? bestLimit = null;
        long? usage = null;

        void Consider(string limitFile, string usageFile, string statFile, string inactiveKey)
        {
            if (ParseLimit(read(limitFile)) is not { } limit || (bestLimit is { } best && limit >= best)) return;
            bestLimit = limit;
            usage = ParseBytes(read(usageFile)) is { } current ? Math.Max(0, current - (StatValue(read(statFile), inactiveKey) ?? 0)) : null;
        }

        foreach (var dir in Ancestors(v2Path))
            Consider($"/sys/fs/cgroup{dir}/memory.max", $"/sys/fs/cgroup{dir}/memory.current", $"/sys/fs/cgroup{dir}/memory.stat", "inactive_file");
        if (v1Path is not null)
            foreach (var dir in Ancestors(v1Path))
                Consider($"/sys/fs/cgroup/memory{dir}/memory.limit_in_bytes", $"/sys/fs/cgroup/memory{dir}/memory.usage_in_bytes", $"/sys/fs/cgroup/memory{dir}/memory.stat", "total_inactive_file");
        else
            Consider("/sys/fs/cgroup/memory/memory.limit_in_bytes", "/sys/fs/cgroup/memory/memory.usage_in_bytes", "/sys/fs/cgroup/memory/memory.stat", "total_inactive_file");
        return usage;
    }

    private static long? ParseBytes(string? text) =>
        long.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>One counter of a <c>memory.stat</c> file ("name value" per line).</summary>
    private static long? StatValue(string? text, string key)
    {
        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 2);
            if (parts.Length == 2 && parts[0] == key) return ParseBytes(parts[1]);
        }
        return null;
    }

    /// <summary>A cgroup limit file: a number of bytes, or <c>max</c> (v2), or a number so large that it means no limit (v1).</summary>
    internal static long? ParseLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text == "max") return null;
        return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 && value < (1L << 60)
            ? value : null;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var current = "/" + path.Trim('/');
        while (true)
        {
            yield return current == "/" ? string.Empty : current;
            if (current == "/") yield break;
            var cut = current.LastIndexOf('/');
            current = cut <= 0 ? "/" : current[..cut];
        }
    }

    private static long? Smaller(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    // ---- Windows: the job object the process runs in (a container, a service host, a launcher that limits memory) ---------------------------------------

    private const uint JobObjectLimitProcessMemory = 0x100;
    private const uint JobObjectLimitJobMemory = 0x200;
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>The memory limit a job object states: the smaller of its per-process and its whole-job limit, or null when it states none.</summary>
    internal static long? JobLimit(uint limitFlags, ulong processMemoryLimit, ulong jobMemoryLimit)
    {
        long? best = null;
        if ((limitFlags & JobObjectLimitProcessMemory) != 0 && processMemoryLimit is > 0 and < (1UL << 60)) best = (long)processMemoryLimit;
        if ((limitFlags & JobObjectLimitJobMemory) != 0 && jobMemoryLimit is > 0 and < (1UL << 60)) best = Smaller(best, (long)jobMemoryLimit);
        return best;
    }

    private static long? WindowsJobLimit()
    {
        // A null job handle means "the job this process is in"; a process in none gets an error, which is "no limit".
        if (!QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation, out var info, Marshal.SizeOf<JobExtended>(), out _)) return null;
        return JobLimit(info.Basic.LimitFlags, info.ProcessMemoryLimit, info.JobMemoryLimit);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasic
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtended
    {
        public JobBasic Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out JobExtended info, int length, out int returned);
}
