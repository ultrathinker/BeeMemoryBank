using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Continuous reconciliation (plan 6.4, 6.8): chain links for retired DEKs and re-sealing of secrets.
/// The node's current DEK is the one it was initialised with; the "retired" key is a fresh one the
/// test plants as if an earlier rotation had replaced it.
/// </summary>
public class RecoveryReconcilerTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass1";
    private readonly ConcreteFixture _f = new();
    private SessionService _session = null!;
    private byte[] _current = null!;
    private readonly byte[] _retired = MasterKeyManager.GenerateMasterDek();
    private readonly string _commitId = Guid.NewGuid().ToString();

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await _f.InitService.InitializeAsync("admin", "Node", AdminPassword);
        await _f.Session.UnlockAsync(AdminPassword);
        _current = _f.Session.GetMasterDek();
    }

    public Task DisposeAsync() => _f.DisposeAsync();

    /// <summary>Plants what DekRewrapper leaves behind after rotating _retired → _current.</summary>
    private async Task PlantRotationAsync(bool withChainMaterial = true)
    {
        var name = IRetiredMasterDekStore.KeyNamePrefix + _commitId;
        var (wrapped, iv) = NodeDataKeyEnvelope.Wrap(name, _retired, _current);
        var (chainEnc, chainIv) = MasterKeyManager.WrapMasterDek(_current, _retired);
        using var conn = _f.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_node_data_key (key_name, wrapped_key, iv, created_at) VALUES (@N, @W, @I, @T)",
            new { N = name, W = wrapped, I = iv, T = DateTime.UtcNow.ToString("O") });
        var now = DateTime.UtcNow.ToString("O");
        await new DekRotationStateRepository(_f.Factory).UpsertAsync(new DekRotationStateRow(
            _commitId, DekRotationState.Applied, _commitId, now, now, null, null, null, null, null, null, now, now));
        if (withChainMaterial)
            await conn.ExecuteAsync(
                "UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = @E, chain_iv = @I WHERE event_id = @Id",
                new { E = Convert.ToBase64String(chainEnc), I = Convert.ToBase64String(chainIv), Id = _commitId });

        // A session that loads persisted retired keys, like the real one.
        _session = new SessionService(new KeySlotRepository(_f.Factory), null, new RetiredMasterDekStore(_f.Factory));
        (await _session.UnlockAsync(AdminPassword)).Should().BeTrue();
    }

    private RecoveryReconciler Reconciler() => new(
        _session, new RetiredMasterDekStore(_f.Factory), _f.Factory,
        new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _session, _f.EventApplier, new SyncTrigger(), new FixedOwnStanding(false)),
        NullLogger<RecoveryReconciler>.Instance);

    private async Task<List<(string Author, string Old, string New, byte[] Wrapped, byte[] Iv)>> Links()
    {
        using var conn = _f.Factory.CreateConnection();
        return (await conn.QueryAsync<(string, string, string, byte[], byte[])>(
            "SELECT author_node_id, old_fingerprint, new_fingerprint, wrapped, iv FROM tbl_dek_retired_link")).ToList();
    }

    private async Task<int> LoggedEvents(string type) => (await _f.EventLogRepo.GetRecentAsync(100, 0, type)).Count;

    [Fact]
    public async Task RetiredKey_WithoutLink_GetsOne_ThatOpensUnderTheNewKey()
    {
        await PlantRotationAsync();

        await Reconciler().ReconcileAsync();

        var link = (await Links()).Should().ContainSingle().Subject;
        link.Old.Should().Be(DekFingerprint.Of(_retired));
        link.New.Should().Be(DekFingerprint.Of(_current));
        MasterKeyManager.UnwrapMasterDek(link.Wrapped, link.Iv, _current).Should().Equal(_retired);
        (await LoggedEvents(EventTypes.RetiredLinkSet)).Should().Be(1);
    }

    [Fact]
    public async Task SecondRun_PublishesNothing()
    {
        await PlantRotationAsync();
        await Reconciler().ReconcileAsync();

        await Reconciler().ReconcileAsync();

        (await LoggedEvents(EventTypes.RetiredLinkSet)).Should().Be(1, "a verified link already exists");
    }

    [Fact]
    public async Task ForgedLinkWithTheRightFingerprints_DoesNotSilenceTheRealOne()
    {
        await PlantRotationAsync();
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_dek_retired_link (commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv, created_at)
                  VALUES (@C, @A, @O, @N, @W, @I, @T)",
                new
                {
                    C = _commitId, A = Guid.NewGuid(), O = DekFingerprint.Of(_retired), N = DekFingerprint.Of(_current),
                    W = new byte[49], I = new byte[12], T = DateTime.UtcNow.ToString("O")
                });

        await Reconciler().ReconcileAsync();

        (await LoggedEvents(EventTypes.RetiredLinkSet)).Should().Be(1);
        (await Links()).Should().HaveCount(2);
    }

    [Fact]
    public async Task RetiredKey_WithoutChainMaterial_IsSkipped()
    {
        await PlantRotationAsync(withChainMaterial: false);

        await Reconciler().ReconcileAsync();

        (await Links()).Should().BeEmpty();
    }

    [Fact]
    public async Task SecretSealedUnderRetiredKey_IsResealedUnderTheCurrentOne()
    {
        await PlantRotationAsync();
        var name = $"restic:{Guid.NewGuid()}";
        var secret = "restic-repo-password"u8.ToArray();
        var (wrapped, iv) = SealedSecretCrypto.Seal(name, secret, _retired);
        await InsertSealAsync(name, DekFingerprint.Of(_retired), wrapped, iv);

        await Reconciler().ReconcileAsync();

        using var conn = _f.Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<(string Fp, byte[] Wrapped, byte[] Iv)>(
            "SELECT dek_fingerprint, wrapped, iv FROM tbl_sealed_secret WHERE name = @N", new { N = name });
        row.Fp.Should().Be(DekFingerprint.Of(_current));
        SealedSecretCrypto.TryOpen(name, row.Wrapped, row.Iv, _current).Should().Equal(secret);
        (await LoggedEvents(EventTypes.SealedSecretSet)).Should().Be(1);
    }

    [Fact]
    public async Task SealThatDoesNotOpen_IsNotCarriedForward_AndDoesNotStopTheOthers()
    {
        // Rows are processed by name: the forged one comes first.
        await PlantRotationAsync();
        var forged = "restic:00000000-0000-0000-0000-000000000001";
        var real = "restic:ffffffff-0000-0000-0000-000000000001";
        await InsertSealAsync(forged, DekFingerprint.Of(_retired), new byte[40], new byte[12]);
        var (wrapped, iv) = SealedSecretCrypto.Seal(real, "pw"u8.ToArray(), _retired);
        await InsertSealAsync(real, DekFingerprint.Of(_retired), wrapped, iv);

        await Reconciler().ReconcileAsync();

        var published = await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.SealedSecretSet);
        published.Should().ContainSingle().Which.Payload.Should().Contain(real);
    }

    [Fact]
    public async Task Unlock_RunsTheReconciler()
    {
        var recorder = new RecordingReconciler();
        var scopes = new ServiceCollection()
            .AddScoped<IRecoveryReconciler>(_ => recorder)
            .AddScoped<INodeIdentityRepository>(_ => _f.NodeRepo)
            .AddScoped<IUserRepository>(_ => new UserRepository(_f.Factory))
            .AddScoped<IKeySlotRepository>(_ => new KeySlotRepository(_f.Factory))
            .AddSingleton<IDbConnectionFactory>(_f.Factory)
            .AddScoped<LegacyPasswordSlotMigrationService>()
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var session = new SessionService(new KeySlotRepository(_f.Factory), scopes);

        (await session.UnlockAsync(AdminPassword)).Should().BeTrue();

        (await Task.WhenAny(recorder.Ran.Task, Task.Delay(TimeSpan.FromSeconds(30))))
            .Should().BeSameAs(recorder.Ran.Task, "unlock must start reconciliation");
    }

    private async Task InsertSealAsync(string name, string fp, byte[] wrapped, byte[] iv)
    {
        using var conn = _f.Factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts, source_node_id)
              VALUES (@N, @F, @W, @I, @T, 'A', 1, @S)",
            new { N = name, F = fp, W = wrapped, I = iv, T = DateTime.UtcNow.ToString("O"), S = Guid.NewGuid() });
    }

    private sealed class RecordingReconciler : IRecoveryReconciler
    {
        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReconcileAsync(CancellationToken ct = default) { Ran.TrySetResult(); return Task.CompletedTask; }
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
