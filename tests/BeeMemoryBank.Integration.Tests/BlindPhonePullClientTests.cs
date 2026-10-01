using System.Reflection;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Real protocol-3 pull from a listening blind node into a phone-shaped blind receiver.</summary>
public sealed class BlindPhonePullClientTests : IDisposable
{
    private readonly BlindNodeFactory _source = new();
    private readonly BlindNodeFactory _phone = new();

    public void Dispose()
    {
        _phone.Dispose();
        _source.Dispose();
    }

    [Fact]
    public async Task Pull_AppliesAVerifiedRemoteEventAndAdvancesTheReceiveCursor()
    {
        var source = (await _source.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var phone = (await _phone.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        await _source.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(phone));
        var (authorPublic, authorSeed) = Ed25519Signer.GenerateKeyPair();
        var author = Guid.NewGuid();
        await _phone.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(author, authorPublic));
        var before = await _source.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var evt = SignedFolderCreate(author, authorSeed, "/PhonePull", before + 1);
        await _source.Services.GetRequiredService<IEventLogRepository>().AppendAsync(evt);
        var served = (await _source.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(before)).Single();
        var pull = new BlindPhonePullClient(
            _phone.Services.GetRequiredService<IServiceScopeFactory>(),
            _phone.Services.GetRequiredService<INodeAuthSigner>(),
            NullLogger<BlindPhonePullClient>.Instance);
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, source.NodeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", source.Ed25519PublicKey, new byte[32]);
        using var http = new HttpClient(_source.Server.CreateHandler());

        await pull.SyncOnceAsync(http, target, CancellationToken.None);

        (await _phone.Services.GetRequiredService<IEventLogRepository>().ExistsAsync(evt.EventId)).Should().BeTrue();
        (await _phone.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(source.NodeId))!
            .LastSequenceNum.Should().Be(served.SequenceNum);
    }

    [Fact]
    public async Task Pull_CompactedRemotePosition_ThrowsSnapshotRequiredException()
    {
        var source = (await _source.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var phone = (await _phone.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        await _source.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(phone));
        using (var connection = _source.Services.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>().CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason) VALUES (@at, NULL, 1, 0, 'test')",
                new { at = DateTime.UtcNow });
        }
        var pull = new BlindPhonePullClient(
            _phone.Services.GetRequiredService<IServiceScopeFactory>(),
            _phone.Services.GetRequiredService<INodeAuthSigner>(),
            NullLogger<BlindPhonePullClient>.Instance);
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, source.NodeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", source.Ed25519PublicKey, new byte[32]);
        using var http = new HttpClient(_source.Server.CreateHandler());

        var act = () => pull.SyncOnceAsync(http, target, CancellationToken.None);

        (await act.Should().ThrowAsync<SnapshotRequiredException>()).Which.ErrorCode
            .Should().Be(SnapshotRequiredException.SequenceTooOldCode);
    }

    [Fact]
    public async Task Pull_RepeatedBadEventQuarantinesItAndOnlyThenAdvancesTheCursor()
    {
        var (source, phone, pull, target) = await PreparePullAsync();
        var (authorPublic, authorSeed) = Ed25519Signer.GenerateKeyPair();
        var author = Guid.NewGuid();
        await _phone.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(author, authorPublic));
        var before = await _source.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var bad = SignedFolderCreate(author, authorSeed, "/Bad", before + 1);
        bad.Signature = new byte[64];
        await _source.Services.GetRequiredService<IEventLogRepository>().AppendAsync(bad);
        var served = (await _source.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(before)).Single();
        using var http = new HttpClient(_source.Server.CreateHandler());

        for (var attempt = 1; attempt <= SyncEventQuarantine.QuarantineThreshold; attempt++)
        {
            await pull.SyncOnceAsync(http, target, CancellationToken.None);
            var position = await _phone.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(source.NodeId);
            position!.LastSequenceNum.Should().Be(attempt == SyncEventQuarantine.QuarantineThreshold ? served.SequenceNum : 0,
                "a non-quarantined bad event must be retried from the same receive cursor");
        }

        (await _phone.Services.GetRequiredService<IEventLogRepository>().ExistsAsync(bad.EventId)).Should().BeFalse();
        using var scope = _phone.Services.CreateScope();
        var entry = (await scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>().GetAllAsync())
            .Single(e => e.EventId == bad.EventId);
        entry.PermanentFailureCount.Should().Be(SyncEventQuarantine.QuarantineThreshold);
    }

    [Fact]
    public async Task Pull_VerifiedPhoneOwnEvent_AdvancesTheCursorWithoutApplyingIt()
    {
        var (source, phone, pull, target) = await PreparePullAsync();
        var before = await _source.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = phone.NodeId,
            LamportTs = before + 1,
            EventType = EventTypes.FolderCreate,
            EntityId = "/Own",
            Payload = JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), "/Own", "Own", "/", DateTime.UtcNow, DateTime.UtcNow)),
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow
        };
        evt.Signature = _phone.Services.GetRequiredService<INodeAuthSigner>().SignChallenge(phone, EventSignature.BuildPayload(evt));
        await _source.Services.GetRequiredService<IEventLogRepository>().AppendAsync(evt);
        var served = (await _source.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(before)).Single();
        using var http = new HttpClient(_source.Server.CreateHandler());

        await pull.SyncOnceAsync(http, target, CancellationToken.None);

        (await _phone.Services.GetRequiredService<IEventLogRepository>().ExistsAsync(evt.EventId)).Should().BeFalse();
        (await _phone.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(source.NodeId))!
            .LastSequenceNum.Should().Be(served.SequenceNum);
    }

    [Fact]
    public async Task Pull_CancelledWhileAnEventIsApplied_StopsBeforeTheNextOneAndQuarantinesNothing()
    {
        var (source, _, _, target) = await PreparePullAsync();
        var (authorPublic, authorSeed) = Ed25519Signer.GenerateKeyPair();
        var author = Guid.NewGuid();
        await _phone.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(author, authorPublic));
        var events = await AppendThreeEventsToTheSourceAsync(author, authorSeed);
        using var stop = new CancellationTokenSource();
        // The worker is told to stop while the first event is being finished (its quarantine row cleared).
        var pull = new BlindPhonePullClient(
            new SubstitutingScopeFactory(_phone.Services.GetRequiredService<IServiceScopeFactory>(), (inner, type) =>
                type == typeof(ISyncQuarantineRepository)
                    ? Intercept((inner.GetService(type) as ISyncQuarantineRepository)!,
                        (method, _) => { if (method.Name == nameof(ISyncQuarantineRepository.ClearAsync)) stop.Cancel(); })
                    : null),
            _phone.Services.GetRequiredService<INodeAuthSigner>(), NullLogger<BlindPhonePullClient>.Instance);
        using var http = new HttpClient(_source.Server.CreateHandler());

        var act = () => pull.SyncOnceAsync(http, target, stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var log = _phone.Services.GetRequiredService<IEventLogRepository>();
        (await log.ExistsAsync(events[0].EventId)).Should().BeTrue("the event being applied when the stop came is finished");
        (await log.ExistsAsync(events[1].EventId)).Should().BeFalse("a stopped worker must not keep applying the page");
        (await _phone.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(source.NodeId))!
            .LastSequenceNum.Should().Be(events[0].SequenceNum, "what was applied is not lost");
        await AssertNothingQuarantinedAsync();
    }

    [Fact]
    public async Task Pull_AnApplyThatThrowsCancellation_IsNotRecordedAsAnEventFailure()
    {
        var (source, _, _, target) = await PreparePullAsync();
        var (authorPublic, authorSeed) = Ed25519Signer.GenerateKeyPair();
        var author = Guid.NewGuid();
        await _phone.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(author, authorPublic));
        var events = await AppendThreeEventsToTheSourceAsync(author, authorSeed);
        // An applier whose folder lookups report cancellation, as a stopped worker's database call would.
        var pull = new BlindPhonePullClient(
            new SubstitutingScopeFactory(_phone.Services.GetRequiredService<IServiceScopeFactory>(), (inner, type) =>
                type == typeof(EventApplier)
                    ? ActivatorUtilities.CreateInstance<EventApplier>(inner,
                        Intercept<IFolderRepository>(null, (_, _) => throw new OperationCanceledException()))
                    : null),
            _phone.Services.GetRequiredService<INodeAuthSigner>(), NullLogger<BlindPhonePullClient>.Instance);
        using var http = new HttpClient(_source.Server.CreateHandler());

        var act = () => pull.SyncOnceAsync(http, target, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await AssertNothingQuarantinedAsync();
        ((await _phone.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(source.NodeId))?.LastSequenceNum ?? 0)
            .Should().Be(0, "nothing was applied, so the cursor does not move");
        (await _phone.Services.GetRequiredService<IEventLogRepository>().ExistsAsync(events[0].EventId)).Should().BeFalse();
    }

    private async Task<List<SyncEvent>> AppendThreeEventsToTheSourceAsync(Guid author, byte[] authorSeed)
    {
        var log = _source.Services.GetRequiredService<IEventLogRepository>();
        var before = await log.GetMaxSequenceAsync();
        foreach (var n in Enumerable.Range(1, 3))
            await log.AppendAsync(SignedFolderCreate(author, authorSeed, $"/Page{n}", before + n));
        return (await log.GetAfterSequenceAsync(before)).ToList();
    }

    private async Task AssertNothingQuarantinedAsync()
    {
        using var scope = _phone.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>().GetAllAsync()).Should().BeEmpty(
            "a stop is not a failure of the event that happened to be running");
    }

    /// <summary>Forwards every call to <paramref name="real"/> (when given) after running <paramref name="before"/>.</summary>
    private static T Intercept<T>(T? real, Action<MethodInfo, object?[]?> before) where T : class
    {
        var proxy = DispatchProxy.Create<T, Interceptor>();
        ((Interceptor)(object)proxy).Handler = (method, args) =>
        {
            before(method, args);
            try { return real is null ? null : method.Invoke(real, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        };
        return proxy;
    }

    public class Interceptor : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod!, args);
    }

    /// <summary>A scope factory whose scopes answer some services from the substitute (null: ask the real scope).</summary>
    private sealed class SubstitutingScopeFactory(IServiceScopeFactory inner, Func<IServiceProvider, Type, object?> substitute) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var scope = inner.CreateScope();
            return new Scope(scope, new Provider(scope.ServiceProvider, substitute));
        }

        private sealed class Scope(IServiceScope real, IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;

            public void Dispose() => real.Dispose();
        }

        private sealed class Provider(IServiceProvider real, Func<IServiceProvider, Type, object?> substitute) : IServiceProvider
        {
            public object? GetService(Type serviceType) => substitute(real, serviceType) ?? real.GetService(serviceType);
        }
    }

    private async Task<(NodeIdentity Source, NodeIdentity Phone, BlindPhonePullClient Pull, BlindCallCode Target)> PreparePullAsync()
    {
        var source = (await _source.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var phone = (await _phone.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        await _source.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Whitelist(phone));
        var pull = new BlindPhonePullClient(
            _phone.Services.GetRequiredService<IServiceScopeFactory>(),
            _phone.Services.GetRequiredService<INodeAuthSigner>(),
            NullLogger<BlindPhonePullClient>.Instance);
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, source.NodeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", source.Ed25519PublicKey, new byte[32]);
        return (source, phone, pull, target);
    }

    private static WhitelistEntry Whitelist(NodeIdentity identity) => Whitelist(identity.NodeId, identity.Ed25519PublicKey);

    private static WhitelistEntry Whitelist(Guid nodeId, byte[] publicKey) => new()
    {
        NodeId = nodeId,
        DisplayName = "peer",
        Ed25519PublicKey = publicKey,
        Status = WhitelistStatuses.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static SyncEvent SignedFolderCreate(Guid author, byte[] seed, string path, long lamport)
    {
        var now = DateTime.UtcNow;
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = author,
            LamportTs = lamport,
            EventType = EventTypes.FolderCreate,
            EntityId = path,
            Payload = JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), path, "PhonePull", "/", now, now)),
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = now
        };
        evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));
        return evt;
    }
}
