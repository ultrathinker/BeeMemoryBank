using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The body of a phone backup (plan section 10): the listening node's fresh signed package, its signature
/// and the phone's own signed events, in the three-file tar <see cref="BlindPhoneBackupBody"/> reads back —
/// the same reader the Windows restore uses. Written only when a restore can use it.
/// </summary>
public sealed class BlindPhonePackageSourceTests
{
    private const long Plenty = 1L << 40;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheBody_HoldsThePackageUntouched_ItsSignature_AndEveryLoggedEventInOrder()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(1200, anchorAt: 700, "A1");
        var package = t.Fetcher.Next(anchors: ["A2", "A1"]);

        await t.Source().CreateAsync(t.Destination, CancellationToken.None);

        var parts = await BlindPhoneBackupBody.ReadAsync(t.Destination, Path.Combine(t.Dir, "read"), BlindPhoneBackupLimits.Default);
        (await File.ReadAllBytesAsync(parts.PackagePath)).Should().Equal(t.Fetcher.PackageBytes,
            "the listener's signature covers these exact bytes");
        parts.Signature.Should().Equal(package.Signature);
        var events = await ReadEventsAsync(parts.EventsPath);
        events.Select(e => e.EventId).Should().Equal(t.EventIds, "every event, in log order, across the pages");
        events[0].Signature.Should().Equal(t.FirstSignature, "the signed bytes travel unchanged");
        File.Exists(package.ArchivePath).Should().BeFalse("the fetched copy is not kept: the body holds it now");
        Directory.GetFiles(t.Dir, "*.events").Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAnAnchorEventOnThePhone_ItWaits_AndDownloadsNothing()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(10, anchorAt: -1, anchorId: null);

        var act = () => t.Source().CreateAsync(t.Destination, CancellationToken.None);

        var message = (await act.Should().ThrowAsync<BlindFeaturePendingException>()).Which.Message;
        message.Should().Contain("anchor");
        // Stage 5: with no computer that holds the master key switched on, nothing ever publishes an anchor, and the line on the
        // phone is all the user sees: it has to name what is missing, not only that something is awaited.
        message.Should().Contain("has to be on", "the wait ends only when a computer with the master key is on and connected");
        t.Fetcher.Calls.Should().Be(0, "a package is worth fetching only when a restore could use it");
        File.Exists(t.Destination).Should().BeFalse();
    }

    [Fact]
    public async Task APackageThatHoldsNoAnchorThePhoneReceived_IsOneToWaitOutNotAFailure_AndNothingStays()
    {
        // The listening node serves a package it built up to half an hour ago, so right after an anchor reached
        // the phone the package can still be the older one. The next try, an hour on, gets a newer package.
        var t = await NewAsync();
        await t.AddEventsAsync(10, anchorAt: 3, "mine");
        var package = t.Fetcher.Next(anchors: ["someone-elses"]);

        var act = () => t.Source().CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<BlindFeaturePendingException>()).Which.Message.Should().Contain("anchor");
        t.NothingLeft(package);
    }

    /// <summary>
    /// Stage 5, read back through the real restore: the Windows restore takes the keys from the boxes of the PACKAGE's
    /// database, not only from the header. A package the listener built before the first superadmin signed in holds none,
    /// and the backup was "made" but ended in "The master password opens none of the recovery boxes.".
    /// </summary>
    [Fact]
    public async Task APackageWithoutARecoveryBox_IsOneToWaitOutNotAFailure_AndNothingStays()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(10, anchorAt: 3, "mine");
        var package = t.Fetcher.Next(anchors: ["mine"], recoveryBoxes: 0);

        var act = () => t.Source().CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<BlindFeaturePendingException>()).Which.Message.Should().Contain("recovery box");
        t.NothingLeft(package);
    }

    [Fact]
    public async Task MoreEventsThanARestoreTakes_AreRefused_AndNothingStays()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(5, anchorAt: 1, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"]);
        var source = t.Source(limits: BlindPhoneBackupLimits.Default with { MaxEvents = 3 });

        var act = () => source.CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("more events");
        t.NothingLeft(package);
    }

    [Fact]
    public async Task EventsLargerThanARestoreTakes_AreRefused_AndNothingStays()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(50, anchorAt: 1, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"]);
        var source = t.Source(limits: BlindPhoneBackupLimits.Default with { MaxEventsBytes = 2048 });

        var act = () => source.CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("events");
        t.NothingLeft(package);
    }

    [Fact]
    public async Task APackageOverWhatARestoreTakes_IsRefused_AndNothingStays()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(5, anchorAt: 1, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"], length: 4096);
        var source = t.Source(limits: BlindPhoneBackupLimits.Default with { MaxPackageBytes = 1024 });

        var act = () => source.CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("package");
        t.NothingLeft(package);
    }

    [Fact]
    public async Task NotEnoughRoom_IsReportedBeforeTheDownload_WithTheSizeTheServerGave()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(5, anchorAt: 1, "A1");
        t.Fetcher.Next(anchors: ["A1"], length: 10_000_000);
        var source = t.Source(freeBytes: _ => 10_000_000);

        var act = () => source.CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("room");
        t.Fetcher.SizeSeen.Should().Be(10_000_000);
        t.Fetcher.Written.Should().BeFalse("the hook ran before a byte was written");
        File.Exists(t.Destination).Should().BeFalse();
    }

    [Fact]
    public async Task NotEnoughRoomForTheBody_AfterTheDownload_IsRefused_AndNothingStays()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(5, anchorAt: 1, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"], length: 100_000);
        var asked = 0;
        // Room for the download (the first question), none left once the archive is on disk (the second).
        var source = t.Source(freeBytes: _ => ++asked == 1 ? Plenty : 150_000);

        var act = () => source.CreateAsync(t.Destination, CancellationToken.None);

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("room");
        asked.Should().Be(2);
        t.NothingLeft(package);
    }

    [Fact]
    public async Task AFailureWhileReadingEvents_LeavesNoPartialBody_NoEventsFile_AndNoFetchedPackage()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(1200, anchorAt: 3, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"]);
        var source = t.Source(failOnPage: 2);

        var act = () => source.CreateAsync(t.Destination, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*page 2*");
        t.NothingLeft(package);
    }

    [Fact]
    public async Task ACancelledRun_LeavesNothingBehind()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(1200, anchorAt: 3, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"]);
        using var cts = new CancellationTokenSource();
        var source = t.Source(afterPage: () => cts.Cancel());

        var act = () => source.CreateAsync(t.Destination, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        t.NothingLeft(package);
    }

    [Fact]
    public async Task AFailureWhileWritingTheBody_RemovesTheHalfWrittenBody()
    {
        var t = await NewAsync();
        await t.AddEventsAsync(5, anchorAt: 1, "A1");
        var package = t.Fetcher.Next(anchors: ["A1"]);
        t.Fetcher.LoseTheArchive = true; // the tar writer creates the body, then cannot read the package

        var act = () => t.Source().CreateAsync(t.Destination, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        t.NothingLeft(package);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<SyncEvent>> ReadEventsAsync(string path)
    {
        var list = new List<SyncEvent>();
        await using var file = File.OpenRead(path);
        await foreach (var e in JsonSerializer.DeserializeAsyncEnumerable<SyncEvent>(file, Web)) list.Add(e!);
        return list;
    }

    private static async Task<Rig> NewAsync()
    {
        DapperConfig.Configure();
        var dir = Path.Combine(Path.GetTempPath(), "bmb-s4-pkgsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        return new Rig(dir, factory);
    }

    private sealed class Rig(string dir, DbConnectionFactory factory)
    {
        public string Dir => dir;
        public string Destination => Path.Combine(dir, "backups", "x.source.tmp");
        public FakeFetcher Fetcher { get; } = new(dir);
        public List<Guid> EventIds { get; } = [];
        public byte[] FirstSignature { get; private set; } = [];

        public async Task AddEventsAsync(int count, int anchorAt, string? anchorId)
        {
            Directory.CreateDirectory(Path.Combine(dir, "backups"));
            var log = new EventLogRepository(factory);
            for (var i = 0; i < count; i++)
            {
                var anchor = i == anchorAt;
                var evt = new SyncEvent
                {
                    EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = i + 1,
                    EventType = anchor ? EventTypes.StateAnchor : EventTypes.FolderCreate,
                    EntityId = "/f" + i,
                    Payload = anchor ? JsonSerializer.Serialize(new StateAnchorPayload(anchorId!, "fp", new Dictionary<string, long>(), "d", "h", "t")) : "{}",
                    Signature = RandomNumberGenerator.GetBytes(64), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
                };
                if (i == 0) FirstSignature = evt.Signature;
                await log.AppendAsync(evt);
                EventIds.Add(evt.EventId);
            }
        }

        public BlindPhonePackageSource Source(Func<string, long>? freeBytes = null, BlindPhoneBackupLimits? limits = null,
            int? failOnPage = null, Action? afterPage = null)
        {
            var services = new ServiceCollection();
            services.AddScoped<IEventLogRepository>(_ => PagingProbe.Wrap(new EventLogRepository(factory), failOnPage, afterPage));
            return new BlindPhonePackageSource(Fetcher, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                freeBytes ?? (_ => Plenty), limits, pageSize: 500);
        }

        public void NothingLeft(VerifiedReplicaPackage package)
        {
            File.Exists(Destination).Should().BeFalse("no partial body may stay for the runner to move into place");
            Directory.GetFiles(dir, "*.events").Should().BeEmpty();
            File.Exists(package.ArchivePath).Should().BeFalse("the fetched package is removed whatever happened");
        }
    }

    private sealed class FakeFetcher(string dir) : IBlindVerifiedPackageFetcher
    {
        private VerifiedReplicaPackage? _next;
        public int Calls { get; private set; }
        public long SizeSeen { get; private set; } = -1;
        public bool Written { get; private set; }
        public byte[] PackageBytes { get; private set; } = [];
        public bool LoseTheArchive { get; set; }

        public VerifiedReplicaPackage Next(string[] anchors, int length = 3000, int recoveryBoxes = 1)
        {
            PackageBytes = RandomNumberGenerator.GetBytes(length);
            var path = Path.Combine(dir, "backup-package.tar.gz");
            _next = new VerifiedReplicaPackage(path, RandomNumberGenerator.GetBytes(64), "sha", length, anchors, recoveryBoxes);
            return _next;
        }

        public Task<VerifiedReplicaPackage> FetchAsync(Action<long> beforeDownload, IProgress<double>? progress, CancellationToken ct)
        {
            Calls++;
            SizeSeen = _next!.Length;
            beforeDownload(_next.Length);
            if (!LoseTheArchive) File.WriteAllBytes(_next.ArchivePath, PackageBytes);
            Written = true;
            return Task.FromResult(_next);
        }
    }

    /// <summary>The real log, with a failure or a callback on a chosen page of the sequential read.</summary>
    public class PagingProbe : DispatchProxy
    {
        public IEventLogRepository Inner = null!;
        public int? FailOnPage;
        public Action? AfterPage;
        private int _page;

        public static IEventLogRepository Wrap(IEventLogRepository inner, int? failOnPage, Action? afterPage)
        {
            var proxy = Create<IEventLogRepository, PagingProbe>();
            var probe = (PagingProbe)(object)proxy;
            probe.Inner = inner;
            probe.FailOnPage = failOnPage;
            probe.AfterPage = afterPage;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            try
            {
                if (method!.Name != nameof(IEventLogRepository.GetAllAfterSequenceAsync))
                    return method.Invoke(Inner, args);
                _page++;
                if (_page == FailOnPage) throw new IOException($"simulated read failure on page {_page}");
                var task = (Task<List<SyncEvent>>)method.Invoke(Inner, args)!;
                return task.ContinueWith(t =>
                {
                    AfterPage?.Invoke();
                    return t.Result;
                }, TaskScheduler.Default);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
