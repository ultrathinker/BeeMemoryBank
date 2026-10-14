using System.Collections.Concurrent;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A real node's stopped data directory with the canary vault of step-a-spec 16.1, built once for a test class (starting the node and its
/// Argon2 take most of the time of a test). <see cref="SealedNode.CreateAsync"/> makes a sealed copy of it per test with the page codec.
/// </summary>
public sealed class SealedNodeTemplate : IAsyncLifetime
{
    public const string Password = StepANode.Password;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-sealed-template-" + Guid.NewGuid().ToString("N"));
    public string PlainDir => Path.Combine(Root, "plain");
    public List<string> Canaries { get; private set; } = [];

    public async Task InitializeAsync()
    {
        await using var node = await StepANode.CreateAsync("SealedTemplate");
        var seeded = await StepANode.SeedWithCanariesAsync(node);
        Canaries = seeded.Canaries;
        using (var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        var chat = Path.Combine(node.DataPath, "chat.db");
        if (File.Exists(chat))
        {
            using var c = new SqliteConnection($"Data Source={chat};Pooling=False");
            c.Open();
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();
        SealedNode.CopyTree(node.DataPath, PlainDir, skipDatabases: false);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// The canary node, sealed: its <c>beememorybank.db</c> and <c>chat.db</c> are SQLCipher files under random page keys, the services are built by
/// hand over a keyed <see cref="DbConnectionFactory"/> (the gate of track 3 is not on this branch), and the vault is unlocked with the password.
/// </summary>
public sealed class SealedNode : IDisposable
{
    private readonly string _root;

    public string Dir { get; }
    public InMemoryVaultKeySource Keys { get; }
    public DbConnectionFactory Factory { get; }
    public SessionService Session { get; }
    public NodeIdentityRepository NodeRepo { get; }
    public SnapshotService Snapshots { get; }
    public IReadOnlyList<string> Canaries { get; }

    private SealedNode(string root, string dir, InMemoryVaultKeySource keys, DbConnectionFactory factory, SessionService session,
        NodeIdentityRepository nodeRepo, SnapshotService snapshots, IReadOnlyList<string> canaries)
    {
        (_root, Dir, Keys, Factory, Session, NodeRepo, Snapshots, Canaries) = (root, dir, keys, factory, session, nodeRepo, snapshots, canaries);
    }

    public static async Task<SealedNode> CreateAsync(SealedNodeTemplate template)
    {
        var root = Path.Combine(Path.GetTempPath(), "bmb-sealed-node-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await BuildAsync(template, root);
        }
        catch
        {
            // Nobody holds a half-built node to dispose: its folder goes here.
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task<SealedNode> BuildAsync(SealedNodeTemplate template, string root)
    {
        var dir = Path.Combine(root, "data");
        CopyTree(template.PlainDir, dir, skipDatabases: true);

        var keys = new InMemoryVaultKeySource();
        foreach (var (file, purpose) in new[] { ("beememorybank.db", DbPurposes.Vault), ("chat.db", DbPurposes.Chat) })
        {
            var source = Path.Combine(template.PlainDir, file);
            if (!File.Exists(source)) continue;
            var key = keys.GetPageKey(purpose);
            try
            {
                await using var image = File.OpenRead(source);
                await PlainImageImporter.ToSealedFileAsync(image, image.Length, Path.Combine(dir, file), key, memoryCap: 256L << 20);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"), keys);
        var session = new SessionService(new KeySlotRepository(factory));
        (await session.UnlockAsync(SealedNodeTemplate.Password)).Should().BeTrue("the sealed copy keeps the canary node's key slots");
        var nodeRepo = new NodeIdentityRepository(factory);
        var snapshots = new SnapshotService(dir, factory, nodeRepo, new NullLamportClock(), keys: new SessionSnapshotKeyOperations(session));
        return new SealedNode(root, dir, keys, factory, session, nodeRepo, snapshots, template.Canaries);
    }

    /// <summary>The same data directory, plain (the control: the scans must find the canaries there).</summary>
    public static async Task<(string Root, string Dir, DbConnectionFactory Factory, SessionService Session, SnapshotService Snapshots)> CreatePlainAsync(
        SealedNodeTemplate template)
    {
        var root = Path.Combine(Path.GetTempPath(), "bmb-plain-node-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "data");
        CopyTree(template.PlainDir, dir, skipDatabases: false);
        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        var session = new SessionService(new KeySlotRepository(factory));
        (await session.UnlockAsync(SealedNodeTemplate.Password)).Should().BeTrue();
        var snapshots = new SnapshotService(dir, factory, new NodeIdentityRepository(factory), new NullLamportClock(), keys: new SessionSnapshotKeyOperations(session));
        return (root, dir, factory, session, snapshots);
    }

    public byte[] CopyDek() => Session.GetMasterDek();

    public static void CopyTree(string from, string to, bool skipDatabases)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(f);
            // The instance lock of a running node is never copied: on Linux it is an exclusive flock that File.Copy cannot read
            // (InstanceGuard), and the copy's own node takes its lock when it starts.
            if (f.EndsWith("-shm") || f.EndsWith("-wal") || name == "node.lock" || name == InstanceGuard.FileName) continue;
            if (skipDatabases && name is "beememorybank.db" or "chat.db") continue;
            File.Copy(f, Path.Combine(to, name));
        }
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)), skipDatabases: false);
    }

    public void Dispose()
    {
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Watches a folder from its own thread while a test runs and records every file that holds a plain SQLite header or a canary, whatever its
/// name (spec 15.1.7-9: "no plain file exists at any moment"). Its own thread, not the pool: a starved pool is how a short-lived staging file is missed.
/// <para>
/// The limit, plainly: this is a poll. A plain file that is created and removed again between two looks (it lives shorter than the polling interval
/// plus the time one look takes, a few milliseconds to some tens of milliseconds on a loaded machine) is not seen, and neither is its content. What the
/// watch proves is that no plain file <em>that lives for at least a look</em> appeared: a file the product keeps until its working copy goes, or one that
/// carries a plain header (a file held for the whole snapshot, or behind a SQLite header, turns the sealed snapshot test red). It does not prove that
/// no plain byte was ever on the disk for a moment.
/// </para>
/// <para>
/// What else the tests look at: the finished archive is scanned for the canaries, the staging folder is asserted empty afterwards (a plain file that
/// outlives the snapshot is found there), and the data folders of sealed nodes are scanned at rest by other tests. None of these sees a plain file that
/// is created and deleted again between two looks; making the poll faster would only move the limit, so it is stated here instead.
/// </para>
/// </summary>
public sealed class PlainFileWatch : IDisposable
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();
    private readonly Thread _thread;
    private readonly object _look = new();
    private readonly Action<string>? _midLook;
    private volatile bool _stop;

    public ConcurrentDictionary<string, byte> Seen { get; } = new();
    public ConcurrentBag<string> Plain { get; } = new();
    public ConcurrentBag<string> CanaryHits { get; } = new();

    /// <param name="midLook">Test seam: called with the file name while a look is half done (the plain header is recorded, the canary hits are not).</param>
    public PlainFileWatch(IEnumerable<string> folders, IEnumerable<string> canaries, Action<string>? midLook = null)
    {
        _midLook = midLook;
        var dirs = folders.ToList();
        var list = canaries.ToList();
        using var started = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            started.Set();
            while (!_stop)
            {
                foreach (var dir in dirs.Where(Directory.Exists))
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        Look(file, list);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        _thread.Start();
        started.Wait();
    }

    // One look at a file, from opening it to recording what it holds, is one step under _look, so Forget can tell that no look is half done.
    private void Look(string file, List<string> canaries)
    {
        var name = Path.GetFileName(file);
        Seen.TryAdd(name, 0);
        lock (_look)
        {
            try
            {
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var head = new byte[16];
                if (s.ReadAtLeast(head, 16, throwOnEndOfStream: false) == 16 && head.AsSpan().SequenceEqual(Magic)) Plain.Add(name);
                _midLook?.Invoke(name);
                s.Position = 0;
                var all = new byte[s.Length];
                s.ReadExactly(all);
                foreach (var hit in MetadataCanaries.Scan(all, canaries, file)) CanaryHits.Add(name + ":" + hit.Encoding);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Drops everything recorded so far, once a deliberately planted file has been deleted. A look that opened the file before it was deleted is
    /// still reading it, and would record its hits after a plain clearing of the lists: this waits for that look, and no later look can open the
    /// deleted file, so nothing of the planted file is recorded after this returns.
    /// </summary>
    public void Forget()
    {
        lock (_look)
        {
            Plain.Clear();
            CanaryHits.Clear();
        }
    }

    public void Stop()
    {
        Thread.Sleep(50);
        _stop = true;
        _thread.Join();
    }

    public void Dispose() => Stop();
}
