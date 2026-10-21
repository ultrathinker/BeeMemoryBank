using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The process "died" here: thrown by <see cref="CrashAt"/> at the chosen step; nothing in the code under test catches it.</summary>
internal sealed class SimulatedCrash(string step) : Exception("simulated crash at " + step)
{
    public string Step { get; } = step;
}

/// <summary>Counts the steps a run passes and throws <see cref="SimulatedCrash"/> at the Nth (0 = the first). With no index it only records.</summary>
internal sealed class CrashAt(int? index = null) : IBlindCrashPoints
{
    private int _count;
    public List<string> Steps { get; } = [];

    public void Reach(string step)
    {
        Steps.Add(step);
        if (index is { } i && _count++ == i) throw new SimulatedCrash(step);
    }
}

/// <summary>A root folder in the temp area, the two in-memory store factories, and hosts built over them: what a head would hold, minus the platform.</summary>
internal sealed class AccountRig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-accounts-tests", Guid.NewGuid().ToString("N"));
    public InMemoryBlindSecretStoreFactory Secrets { get; } = new();
    public InMemoryBlindStateStoreFactory States { get; } = new();
    public string LegacyDb => Path.Combine(Root, "beememorybank.db");
    public Dictionary<string, FakeBlindHub> HubByAccount { get; } = [];

    public AccountRig()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>A host over this rig's root and stores; every option can be replaced.</summary>
    public BlindAccountHost Host(Action<BlindAccountHostOptions>? configure = null, IBlindCrashPoints? crash = null,
        int maxAccounts = BlindAccountHost.DefaultMaxAccounts, IBlindAccountLeaveHook? leave = null,
        Func<BlindAccountEntry, Func<HttpMessageHandler>?>? handler = null, BlindDeviceGate? gate = null,
        Action<IServiceCollection, BlindAccountEntry>? overrides = null, IBlindSecretStoreFactory? secrets = null,
        TimeSpan? drain = null, IBlindAccountLifecycle? lifecycle = null, TimeSpan? leaveTimeout = null,
        TimeProvider? time = null, IBlindDeviceConditions? conditions = null)
    {
        var options = new BlindAccountHostOptions
        {
            RootDirectory = Root,
            LegacyDatabasePath = LegacyDb,
            Secrets = secrets ?? Secrets,
            States = States,
            DisplayNameFactory = () => "Test device",
            CrashPoints = crash,
            MaxAccounts = maxAccounts,
            LeaveHook = leave,
            PrimaryHandlerFactory = handler,
            DeviceGate = gate,
            ConfigureOverrides = overrides,
            DrainTimeout = drain ?? TimeSpan.FromSeconds(10),
            LeaveTimeout = leaveTimeout ?? TimeSpan.FromMilliseconds(300),
            Lifecycle = lifecycle,
            Conditions = conditions,
            Time = time ?? TimeProvider.System,
            RootLockWait = TimeSpan.FromMilliseconds(150),
        };
        configure?.Invoke(options);
        return new BlindAccountHost(options);
    }

    /// <summary>
    /// The bytes of every file under <paramref name="folder"/>, by relative path: for "nothing was touched" checks. <paramref name="beforeOpen"/> is called with each file just before it is
    /// opened (a test makes the moment between the listing and the open happen where it wants).
    /// </summary>
    public static Dictionary<string, string> Fingerprint(string folder, bool databaseSidecars = true, Action<string>? beforeOpen = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            // SQLite's write-ahead and shared-memory files come and go with its connections (any test's ClearAllPools ends them): not data a test compares.
            if (!databaseSidecars && (file.EndsWith("-wal", StringComparison.Ordinal) || file.EndsWith("-shm", StringComparison.Ordinal))) continue;
            // The lock file is held open (and empty) by a running host: it is not data.
            if (Path.GetFileName(file) == "accounts.lock") continue;
            beforeOpen?.Invoke(file);
            // A file that is gone between the listing and the open (a write-ahead file whose last connection closed) is a file the folder no longer has: the same as listing it a moment later.
            // One whose deletion lands in that same window (a pool-clear closing what it takes to be the last connection while a connection opened moments before still holds the file) answers
            // "access denied" when it is opened: the file is on its way out, and it is the same a-moment-later listing.
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                result[Path.GetRelativePath(folder, file)] = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A blind node that answers the pull protocol in memory: challenge, authenticate, events, report-position. It stands at the bottom of an account's real HTTP pipeline
/// (<see cref="BlindAppOptions.PrimaryHandlerFactory"/>), so the account's own pull client, signer, applier and database run for real. It records every request it
/// gets, which is what the isolation tests read. Each hub has its own node id, key, pin and canary.
/// </summary>
internal sealed class FakeBlindHub : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly byte[] _privateKey;
    private readonly object _gate = new();
    private long _lamport = 100;
    private readonly List<SyncEvent> _events = [];
    private readonly List<Seen> _seen = [];

    public sealed record Seen(string Method, string Url, string Body, string Headers)
    {
        public string All => $"{Method} {Url}\n{Headers}\n{Body}";
    }

    public FakeBlindHub(string host)
    {
        Origin = $"https://{host}:5610";
        (PublicKey, _privateKey) = Ed25519Signer.GenerateKeyPair();
        Pin = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        Canary = MetadataCanaries.Ascii("folder" + host.Replace(".", ""));
    }

    public Guid NodeId { get; } = Guid.NewGuid();
    public byte[] PublicKey { get; }
    public string Origin { get; }
    public string Pin { get; }

    /// <summary>This hub's signature over <paramref name="payload"/> (an invitation it issues is signed this way).</summary>
    public byte[] Sign(byte[] payload) => Ed25519Signer.Sign(_privateKey, payload);

    /// <summary>The folder name this hub's events carry: a canary that must reach its own account's database and no other.</summary>
    public string Canary { get; }

    /// <summary>When set, <c>GET /api/sync/events</c> waits here (the test says when the hub answers).</summary>
    public TaskCompletionSource? HoldEvents { get; set; }

    public TaskCompletionSource EventsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<Seen> Requests { get { lock (_gate) return [.. _seen]; } }

    public string AllRequestText() => string.Join("\n----\n", Requests.Select(r => r.All));

    /// <summary>The node ids that authenticated here.</summary>
    public IReadOnlyList<Guid> Callers() => Requests
        .Where(r => r.Url.EndsWith("/api/sync/authenticate", StringComparison.Ordinal))
        .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("nodeId").GetGuid()).ToList();

    /// <summary>The whitelist row an account needs to accept this hub's events: the hub is a superadmin author with this key.</summary>
    public WhitelistEntry WhitelistRow() => new()
    {
        NodeId = NodeId, DisplayName = "Hub", Ed25519PublicKey = PublicKey, Status = "A", IsSuperadmin = true,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = NodeId,
    };

    /// <summary>The call code this hub gives an account whose pairing secret is <paramref name="pairingSecret"/>.</summary>
    public string CallCodeFor(byte[] pairingSecret) => BlindCallCode.Create(Origin, NodeId, Pin, PublicKey, pairingSecret).ToString();

    /// <summary>Adds a signed folder_create event named <paramref name="name"/> (this hub's canary by default).</summary>
    public SyncEvent AddFolder(string? name = null)
    {
        name ??= Canary;
        var evt = new SyncEvent
        {
            EventType = EventTypes.FolderCreate, ProtocolVersion = 3,
            Payload = JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), "/" + name, name, "/", DateTime.UtcNow, DateTime.UtcNow)),
            EventId = Guid.NewGuid(), NodeId = NodeId, CreatedAt = DateTime.UtcNow,
        };
        lock (_gate)
        {
            evt.LamportTs = ++_lamport;
            evt.SequenceNum = _events.Count + 1;
            evt.Signature = Ed25519Signer.Sign(_privateKey, EventSignature.BuildPayload(evt));
            _events.Add(evt);
        }
        return evt;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = string.Join("\n", request.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        lock (_gate) _seen.Add(new Seen(request.Method.Method, url.ToString(), body, headers));

        switch (url.AbsolutePath)
        {
            case "/api/sync/challenge":
                return Json(new { challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), serverNodeId = NodeId });
            case "/api/sync/authenticate":
                return Json(new { token = "token-" + Guid.NewGuid().ToString("N") });
            case "/api/sync/events":
            {
                if (HoldEvents is { } hold)
                {
                    EventsEntered.TrySetResult();
                    await hold.Task.WaitAsync(ct);
                }
                var after = long.Parse(System.Web.HttpUtility.ParseQueryString(url.Query)["afterSequence"] ?? "0");
                List<SyncEvent> page;
                lock (_gate) page = _events.Where(e => e.SequenceNum > after).ToList();
                return Json(page);
            }
            case "/api/sync/report-position":
                return Json(new { ok = true });
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Web), Encoding.UTF8, "application/json"),
    };
}

internal static class AccountTestExtensions
{
    /// <summary>The account's own services, by type.</summary>
    public static T Get<T>(this BlindAccountHost host, string accountId) where T : notnull =>
        host.ServicesFor(accountId).GetRequiredService<T>();

    /// <summary>Gives the account's database the hub's whitelist row and marks the first load as done: what a first load leaves.</summary>
    public static async Task SeedHubAsync(this BlindAccountHost host, string accountId, FakeBlindHub hub)
    {
        var services = host.ServicesFor(accountId);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(hub.WhitelistRow());
        services.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
    }

    /// <summary>The names of the folders in the account's database.</summary>
    public static async Task<List<string>> FolderNamesAsync(this BlindAccountHost host, string accountId)
    {
        using var conn = host.ServicesFor(accountId).GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<string>("SELECT name FROM tbl_folder")).ToList();
    }
}

/// <summary>Looks for the forms a secret or an id can take in a file or a request.</summary>
internal static class LeakScan
{
    /// <summary>The text forms of a node id and of a secret's bytes, and the raw bytes: what to look for.</summary>
    public static IEnumerable<(string Label, byte[] Needle)> Needles(string label, Guid id)
    {
        yield return (label + " (D)", Encoding.UTF8.GetBytes(id.ToString("D")));
        yield return (label + " (D upper)", Encoding.UTF8.GetBytes(id.ToString("D").ToUpperInvariant()));
        yield return (label + " (N)", Encoding.UTF8.GetBytes(id.ToString("N")));
        yield return (label + " (utf16)", Encoding.Unicode.GetBytes(id.ToString("D")));
        yield return (label + " (raw)", id.ToByteArray());
    }

    public static IEnumerable<(string Label, byte[] Needle)> Needles(string label, byte[] secret)
    {
        yield return (label + " (raw)", secret);
        yield return (label + " (base64)", Encoding.ASCII.GetBytes(Convert.ToBase64String(secret)));
        yield return (label + " (base64url)", Encoding.ASCII.GetBytes(Base64Url.EncodeToString(secret)));
        yield return (label + " (hex)", Encoding.ASCII.GetBytes(Convert.ToHexString(secret)));
        yield return (label + " (hex lower)", Encoding.ASCII.GetBytes(Convert.ToHexString(secret).ToLowerInvariant()));
    }

    public static IEnumerable<(string Label, byte[] Needle)> Needles(string label, string text) =>
        MetadataCanaries.Forms(text).Select(f => (label + " (" + f.Name + ")", f.Bytes));

    /// <summary>The labels of the needles found in any file below <paramref name="folder"/>.</summary>
    public static List<string> InFolder(string folder, IEnumerable<(string Label, byte[] Needle)> needles)
    {
        var list = needles.ToList();
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "accounts.lock") continue;   // held open by a running host, and empty
            byte[] bytes;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;   // a live database's -wal can vanish between the listing and the open (a pool-clear closed what it took to be the last connection), and a deletion still under way
                            // answers "access denied" at that open: both are a file the folder no longer has, the same as a listing a moment later. What the -wal held stands in the database
                            // after the checkpoint that went with its deletion; the torn case (the database read before that, the -wal gone after) is why a scan of a live pair checkpoints first.
            }
            foreach (var (label, needle) in list)
                if (needle.Length >= 8 && bytes.AsSpan().IndexOf(needle) >= 0) found.Add($"{label} in {Path.GetFileName(file)}");
        }
        return found;
    }

    public static List<string> InText(string text, IEnumerable<(string Label, byte[] Needle)> needles)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return needles.Where(n => n.Needle.Length >= 8 && bytes.AsSpan().IndexOf(n.Needle) >= 0).Select(n => n.Label).ToList();
    }
}

/// <summary>A lifecycle that stops nothing: for compositions a test builds by hand.</summary>
internal sealed class NoopLifecycle : IBlindLifecycle
{
    public void StopBackgroundWork() { }
    public void StopBackupService() { }
    public void RestartAfterWipe() { }
}

internal static class Legacy26
{
    /// <summary>What a 2.6.4 install leaves: the single-account composition over the root, its identity, a pairing with a hub, media and a backup file.</summary>
    public static async Task<(Guid NodeId, byte[] Seed, FakeBlindHub Hub)> MakeAsync(AccountRig rig)
    {
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        var hub = new FakeBlindHub("legacy-hub.test");
        var services = new ServiceCollection();
        services.AddSingleton(rig.Secrets.For(legacy));
        services.AddSingleton(rig.States.For(legacy));
        services.AddSingleton<IBlindLifecycle, NoopLifecycle>();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(rig.Root, rig.LegacyDb, null, () => "Old phone"));
        await using (var provider = services.BuildServiceProvider())
        {
            var controller = provider.GetRequiredService<BlindAppController>();
            await controller.InitializeAsync();
            controller.AcceptCallCode(hub.CallCodeFor(rig.Secrets.For(legacy).LoadPairingSecret()!)).Should().BeNull();
            provider.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
        }
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(Path.Combine(rig.Root, "media"));
        File.WriteAllBytes(Path.Combine(rig.Root, "media", "photo.bin"), [1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(rig.Root, "blind-backups"));
        File.WriteAllBytes(Path.Combine(rig.Root, "blind-backups", "backup-1.bmbandroid"), [9, 9, 9]);
        var state = new BlindPhoneState(rig.States.For(legacy));
        return (state.NodeId!.Value, rig.Secrets.For(legacy).LoadIdentitySeed()!, hub);
    }
}

/// <summary>Throws <see cref="SimulatedCrash"/> at the first time the named step is reached.</summary>
internal sealed class CrashAtNamed(string step) : IBlindCrashPoints
{
    public void Reach(string reached)
    {
        if (reached == step) throw new SimulatedCrash(reached);
    }
}

/// <summary>Blocks the caller at the named step, once, until the test releases it: an add or a removal held half way, at a place the test chooses.</summary>
internal sealed class PauseAtStep(string step) : IBlindCrashPoints
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _hit;

    /// <summary>Completes when a caller has reached the step (and is waiting there).</summary>
    public Task Reached => _reached.Task;

    public void Release() => _release.Set();

    public void Reach(string reached)
    {
        if (reached != step || Interlocked.Exchange(ref _hit, 1) == 1) return;
        _reached.TrySetResult();
        _release.Wait(TimeSpan.FromSeconds(30));
    }
}

/// <summary>Hands every step to each of the given crash points: an add held at one place and a removal held at another, in one host.</summary>
internal sealed class AllOfCrashPoints(params IBlindCrashPoints[] points) : IBlindCrashPoints
{
    public void Reach(string reached)
    {
        foreach (var point in points) point.Reach(reached);
    }
}

/// <summary>
/// Records the steps a host passes, in order, and lets a test wait for one: a probe step that a task reaches at a deterministic place, and a note the test writes into the same list ("released"),
/// so that what happened before and after can be read off the order and not guessed from the clock.
/// </summary>
internal sealed class StepProbe : IBlindCrashPoints
{
    private readonly object _gate = new();
    private readonly List<string> _steps = [];
    private readonly Dictionary<string, TaskCompletionSource> _waiting = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Steps { get { lock (_gate) return [.. _steps]; } }

    public Task Reached(string step)
    {
        lock (_gate)
        {
            if (_steps.Contains(step)) return Task.CompletedTask;
            if (!_waiting.TryGetValue(step, out var waiting)) _waiting[step] = waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return waiting.Task;
        }
    }

    /// <summary>The test's own mark in the order of the steps.</summary>
    public void Note(string note) => Reach(note);

    public void Reach(string step)
    {
        lock (_gate)
        {
            _steps.Add(step);
            if (_waiting.TryGetValue(step, out var waiting)) waiting.TrySetResult();
        }
    }

    /// <summary>
    /// Asserts that a step is NOT reached within <paramref name="grace"/>. The point it is asked at is deterministic (another probe step, reached before), the grace is only the time a regression would
    /// need to show itself: a task that did not wait for what it must wait for passes the step in a few milliseconds, and this fails at once; a task that does wait costs the grace.
    /// </summary>
    public async Task AssertNotReachedAsync(string step, TimeSpan grace, string because)
    {
        var act = async () => await Reached(step).WaitAsync(grace);
        await act.Should().ThrowAsync<TimeoutException>(because);
    }
}

/// <summary>Makes the next registry writes fail like a file that is held for a moment (an <see cref="IOException"/> before anything is written): a registry that throws once, or for as long as the test wants.</summary>
internal sealed class FailRegistryWrites : IBlindCrashPoints
{
    private int _remaining;
    private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _exhausted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a write has failed because of this.</summary>
    public Task Failed => _failed.Task;

    /// <summary>Completes when the last of the writes that were to fail has failed.</summary>
    public Task Exhausted => _exhausted.Task;

    /// <summary>How many of the next writes fail.</summary>
    public void FailNext(int times) => Volatile.Write(ref _remaining, times);

    public void Stop() => Volatile.Write(ref _remaining, 0);

    public void Reach(string step)
    {
        if (step != "registry:before-write") return;
        int left;
        while (true)
        {
            left = Volatile.Read(ref _remaining);
            if (left <= 0) return;
            if (Interlocked.CompareExchange(ref _remaining, left - 1, left) == left) break;
        }
        _failed.TrySetResult();
        if (left == 1) _exhausted.TrySetResult();
        throw new IOException("the registry file is held for a moment");
    }
}

/// <summary>Cancels a token when the named step is reached: a start that is cancelled half way, at a place the test chooses.</summary>
internal sealed class CancelAtStep(string step, CancellationTokenSource source) : IBlindCrashPoints
{
    public void Reach(string reached)
    {
        if (reached == step) source.Cancel();
    }
}

/// <summary>Makes links for the tests of what the app does when a folder is a link. Where this machine refuses (no privilege, no support), the caller returns early and the Linux run carries the check.</summary>
internal static class TestLinks
{
    /// <summary>A symbolic link to a directory, or on Windows (where that needs a privilege) a junction. False when neither could be made.</summary>
    public static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            process!.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
    }

    /// <summary>A symbolic link to a file. False where that is not allowed (Windows without the privilege).</summary>
    public static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A directory link whose target is made first and then taken away: a link that points at nothing.</summary>
    public static bool TryCreateDanglingDirectoryLink(string link, string scratchTarget)
    {
        Directory.CreateDirectory(scratchTarget);
        if (!TryCreateDirectoryLink(link, scratchTarget)) return false;
        Directory.Delete(scratchTarget);
        return true;
    }

    /// <summary>True when the path itself is a link or a junction (a dangling one too).</summary>
    public static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null || (info.Attributes != (FileAttributes)(-1) && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}

/// <summary>Throws <see cref="SimulatedCrash"/> at the named step, but only once <see cref="Armed"/> is set (a crash in the work a test does AFTER the host is up).</summary>
internal sealed class ArmedCrash(string step) : IBlindCrashPoints
{
    public volatile bool Armed;

    public void Reach(string reached)
    {
        if (Armed && reached == step) throw new SimulatedCrash(reached);
    }
}
