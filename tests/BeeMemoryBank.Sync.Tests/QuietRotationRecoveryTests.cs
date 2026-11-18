using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A blind or backup-folder restore after two quiet rotations that nobody signed in between (release 2.12, plan WS-A3/A4): the recovery
/// material of the node — the device box each rotation published again from the sealed door, and the links each rotation wrote itself —
/// opens the current key with the superadmin's usual password, and through the links every older key, so old content opens too.
/// </summary>
public class QuietRotationRecoveryTests : SyncTestFixture
{
    private RecoveryEventPublisher Events() =>
        new(NodeRepo, EventLogRepo, Clock, Session, EventApplier, new SyncTrigger(), new FixedOwnStanding(false));

    private async Task RotateAsPeerAsync(byte[] newDek, int epoch)
    {
        var commitId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("O");
        await new DekRotationStateRepository(Factory).UpsertAsync(new DekRotationStateRow(commitId, DekRotationState.Committing, Guid.NewGuid().ToString(),
            now, null, null, null, null, null, null, null, now, now));
        await DekRewrapper.RewrapAllAsync(Factory, Session, Session.GetMasterDek(), (byte[])newDek.Clone(), epoch, commitId, isInitiator: false,
            sealedHooks: BeeMemoryBank.Vault.Sealed.SealedRotationHooks.None);
        // What PostRotationRecovery does after the commit: the rotation's link, then the device box of the doors.
        var slots = new KeySlotRepository(Factory);
        await new RecoveryReconciler(Session, new RetiredMasterDekStore(Factory), Factory, Events(), NullLogger<RecoveryReconciler>.Instance).ReconcileAsync();
        var current = Session.GetMasterDek();
        await new DeviceBoxPublisher(Events(), NodeRepo, NullLogger<DeviceBoxPublisher>.Instance, WhitelistRepo, slots, Factory).RepublishForDoorsAsync(current);
        Array.Clear(current);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<RecoveryReconciler>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ARotationThatWroteItsOwnLink_IsNotReportedAsHavingNoChainMaterial()
    {
        // without a v1 door a 2.12 rotation keeps no chain, and it wrote its link itself; the line "no link can be
        // published" was true of nothing and only alarmed.
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        await RotateAsPeerAsync(RandomNumberGenerator.GetBytes(32), epoch: 2);
        var log = new ListLogger();

        await new RecoveryReconciler(Session, new RetiredMasterDekStore(Factory), Factory, Events(), log).ReconcileAsync();

        log.Lines.Should().NotContain(l => l.Contains("no local chain material"));
    }

    [Fact]
    public async Task TwoRotationsWithNobodySigningIn_TheSuperadminPasswordOpensTheCurrentKey_AndEveryOlderOne()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var dek1 = Session.GetMasterDek();
        var slots = new KeySlotRepository(Factory);
        var admin = (await slots.GetAllAsync()).Single(s => s.SlotType == KeySlotTypes.User);
        admin.DoorVersion.Should().Be(2);
        await new DeviceBoxPublisher(Events(), NodeRepo, NullLogger<DeviceBoxPublisher>.Instance, WhitelistRepo, slots, Factory)
            .PublishDeviceBoxAsync(admin, dek1);
        var dek2 = RandomNumberGenerator.GetBytes(32);
        var dek3 = RandomNumberGenerator.GetBytes(32);

        await RotateAsPeerAsync(dek2, epoch: 2);
        await RotateAsPeerAsync(dek3, epoch: 3);

        RecoverySet set;
        using (var conn = Factory.CreateConnection())
        {
            set = await RecoverySetBuilder.BuildAsync(conn);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE chain_encrypted_new_dek IS NOT NULL"))
                .Should().Be(0, "no v1 door: no chain");
        }
        set.Boxes.Should().ContainSingle(b => b.Kind == RecoveryBoxKdf.KindDevice, "one active device box per node")
            .Which.DekFingerprint.Should().Be(DekFingerprint.Of(dek3), "the box was published again from the sealed door at each rotation");
        set.Links.Should().HaveCount(2, "each rotation wrote and published its own link");

        using var keys = await RecoveryKeyResolver.ResolveAsync(set, Password);

        keys.Should().NotBeNull();
        keys!.Current.Should().Equal(dek3);
        keys.Retired.Should().ContainKey(DekFingerprint.Of(dek2)).And.ContainKey(DekFingerprint.Of(dek1), "old content opens through the links");
        keys.HeadProven.Should().BeTrue();
        Array.Clear(dek1);
    }
}
