using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Red tests 15.2.8 (docs/design/step-b-spec.md 11.0, 11.2): the host role, a plain server that never used the checkbox, the three parts that
/// must agree, the four states at start, the file-level restore cases with the operator's key file still mounted, enable and disable. No test
/// here depends on the platform except the permission assertion of the key file (a Windows ACL check and a Unix mode check, each skipped on the
/// other family).
/// </summary>
public class ServerAutoUnlockTests : SealedWriterFixture
{
    private string DataDir => Path.Combine(BootDir, "data");

    private readonly List<(LogLevel Level, string Message)> _logs = [];

    private sealed class Logger(List<(LogLevel, string)> sink) : ILogger<ServerAutoUnlockService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>The physical store, counting every call: a server that never used the checkbox must touch no key file.</summary>
    private sealed class ProbeKeyFiles : IServerKeyFileStore
    {
        private readonly PhysicalServerKeyFileStore _inner = new();
        /// <summary>Looks at a key file or reads it.</summary>
        public int Calls { get; private set; }
        /// <summary>Wipes the node-created file (removing a leftover is not reading a key).</summary>
        public int Wipes { get; private set; }
        public bool Exists(string path) { Calls++; return _inner.Exists(path); }
        public byte[]? ReadSecret(string path, out string? problem) { Calls++; return _inner.ReadSecret(path, out problem); }
        public void WriteNew(string path, ReadOnlySpan<byte> secret) { Calls++; _inner.WriteNew(path, secret); }
        public void WipeAndDelete(string path) { Wipes++; _inner.WipeAndDelete(path); }
    }

    private ServerAutoUnlockService Make(HostRole role = HostRole.Server, SessionService? session = null, ISealedAutoUnlockHost? host = null,
        bool sealedNode = false, string? configuredKeyFile = null, IServerKeyFileStore? keyFiles = null, IKeySlotRepository? slots = null)
    {
        Directory.CreateDirectory(DataDir);
        var scopes = new ServiceCollection().AddSingleton<IAuditLogRepository>(_ => new AuditLogRepository(Factory)).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new ServerAutoUnlockService(slots ?? KeySlots, session ?? Session, DataDir, role, Factory,
            sealedNode ? Mirror : null, host, sealedNode ? Boot : null, scopes, new Logger(_logs), keyFiles, () => configuredKeyFile);
    }

    private async Task<List<MasterKeyStore>> ServerSlotsAsync() =>
        (await KeySlots.GetAllAsync()).Where(s => s.SlotType == ServerAutoUnlockService.SlotType).ToList();

    private async Task<List<string>> AuditActionsAsync()
    {
        using var c = Factory.CreateConnection();
        return (await c.QueryAsync<string>("SELECT action FROM tbl_audit_log WHERE action LIKE 'server_auto_unlock%' ORDER BY id")).ToList();
    }

    private string OperatorKeyFile(string? hex = null)
    {
        var dir = Path.Combine(BootDir, "secrets");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bmb-key");
        // What a careful operator makes: a file only its owner can read (a Windows ACL without Users, a Unix mode 0600).
        OwnerOnlyFile.WriteNew(path, Encoding.ASCII.GetBytes(hex ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
        return path;
    }

    // --- host role ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, HostRole.Server)]
    [InlineData("", HostRole.Server)]
    [InlineData("server", HostRole.Server)]
    [InlineData("desktop ", HostRole.Server)]
    [InlineData(" desktop", HostRole.Server)]
    [InlineData("1", HostRole.Server)]
    [InlineData("true", HostRole.Server)]
    [InlineData("desktop", HostRole.Desktop)]
    [InlineData("DESKTOP", HostRole.Desktop)]
    [InlineData("Desktop", HostRole.Desktop)]
    public void OnlyTheWordDesktopMakesADesktop_EverythingElseIsAServer(string? value, HostRole expected)
    {
        HostRoles.Parse(value).Should().Be(expected);
        default(HostRole).Should().Be(HostRole.Server, "when in doubt the stricter warnings apply");
    }

    [Fact]
    public async Task ADesktopNeverUnlocksWithTheServerSlot_AndCannotEnableIt()
    {
        var server = Make();
        await server.EnableAsync();
        var desktop = Make(HostRole.Desktop, new SessionService(KeySlots));

        (await desktop.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.NotApplicable);
        var enable = async () => await desktop.EnableAsync();
        (await enable.Should().ThrowAsync<ServerAutoUnlockException>()).Which.Code.Should().Be("role_desktop");
    }

    // --- a plain server that never used the checkbox starts as 2.5 does -----------------------------------------------------------

    [Fact]
    public async Task APlainServerThatNeverUsedTheCheckbox_ReadsNoKeyFile_CreatesNoBootStore_AndWritesNothing_EvenWithTheKeyFileVariableSet()
    {
        var existing = OperatorKeyFile();
        var probe = new ProbeKeyFiles();
        Gate.IsSealed = false;
        var svc = Make(configuredKeyFile: existing, keyFiles: probe);
        var slotsBefore = (await KeySlots.GetAllAsync()).Select(s => s.SlotId).ToList();
        var versionBefore = await SealedVersionAsync();

        (await svc.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.NotApplicable);

        probe.Calls.Should().Be(0, "the key file is not even looked at");
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Off);
        var status = await svc.GetStatusAsync();
        status.Enabled.Should().BeFalse();
        status.State.Should().Be(ServerAutoUnlockState.Off);
        probe.Calls.Should().Be(0);
        Directory.Exists(DataDir).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(DataDir).Should().BeEmpty("no record, no key file, no boot store in the data directory");
        (await ServerSlotsAsync()).Should().BeEmpty();
        (await KeySlots.GetAllAsync()).Select(s => s.SlotId).Should().Equal(slotsBefore);
        (await SealedVersionAsync()).Should().Be(versionBefore, "nothing was written to the database either");
        _logs.Should().Contain(l => l.Level == LogLevel.Information && l.Message.Contains("BMB_AUTO_UNLOCK_KEY_FILE") && l.Message.Contains("off"),
            "a key file configured without a slot is inert and says so");
        _logs.Should().NotContain(l => l.Message.Contains(existing), "no path in the log");
    }

    [Fact]
    public async Task AKeyFileThatAppearedByItself_WithoutTheSlotAndTheRecord_IsInert()
    {
        var probe = new ProbeKeyFiles();
        var svc = Make(keyFiles: probe);
        await File.WriteAllTextAsync(svc.NodeKeyFilePath, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

        (await svc.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.NotApplicable);

        Session.Lock();
        (await svc.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.NotApplicable);
        Session.IsUnlocked.Should().BeFalse();
        probe.Calls.Should().Be(0);
    }

    // --- enable ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnableCreatesOneSlot_AnOwnerOnlyKeyFile_AndLast_TheEnableRecord()
    {
        var svc = Make();

        await svc.EnableAsync();

        var slot = (await ServerSlotsAsync()).Should().ContainSingle().Subject;
        slot.Salt.Should().BeNull("the password path never tries this slot");
        slot.ArgonMemory.Should().BeNull();
        File.Exists(svc.NodeKeyFilePath).Should().BeTrue();
        File.ReadAllText(svc.NodeKeyFilePath).Trim().Should().MatchRegex("^[0-9a-f]{64}$");
        var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(svc.RecordPath)).RootElement;
        record.GetProperty("v").GetInt32().Should().Be(1);
        record.GetProperty("slot_sha256").GetString().Should().Be(BootDoorHash.OfSlot(slot.SlotId, slot.SlotType, slot.EncryptedMasterDek, slot.IV, slot.Salt, null, null, null));
        record.GetProperty("dek_fp").GetString().Should().Be(DekFingerprint.Of(Session.GetMasterDek()));
        File.ReadAllText(svc.RecordPath).Should().NotContain(File.ReadAllText(svc.NodeKeyFilePath).Trim(), "the record holds no secret");
        (await AuditActionsAsync()).Should().Equal("server_auto_unlock_enabled");
        (await svc.GetStatusAsync()).Enabled.Should().BeTrue();
    }

    [WindowsAclFact]
    [SupportedOSPlatform("windows")]
    public async Task TheNodeCreatedKeyFileIsOwnerOnly_OnWindows()
    {
        var svc = Make();
        await svc.EnableAsync();

        WindowsAcl.UsersCanRead(svc.NodeKeyFilePath).Should().BeFalse();
        WindowsAcl.OwnerHasFullControl(svc.NodeKeyFilePath).Should().BeTrue();
        OwnerOnlyFile.IsOwnerOnly(svc.NodeKeyFilePath).Should().BeTrue();
    }

    [UnixModeFact]
    public async Task TheNodeCreatedKeyFileIsMode0600_OnLinuxAndMacOs()
    {
        var svc = Make();
        await svc.EnableAsync();

        File.GetUnixFileMode(svc.NodeKeyFilePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task EnableNeedsAnUnlockedSession()
    {
        Session.Lock();
        var svc = Make();

        var act = async () => await svc.EnableAsync();

        await act.Should().ThrowAsync<SessionLockedException>();
        Directory.EnumerateFileSystemEntries(DataDir).Should().BeEmpty();
        (await ServerSlotsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AfterEnableARestartOpensTheVaultWithoutAPassword_OnAPlainServer()
    {
        var dek = Session.GetMasterDek();
        await Make().EnableAsync();

        var restarted = new SessionService(KeySlots);
        restarted.IsUnlocked.Should().BeFalse();
        var svc = Make(session: restarted);

        (await svc.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);

        restarted.IsUnlocked.Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(dek);
    }

    [Fact]
    public async Task AfterEnableARestartOpensTheVaultWithoutAPassword_OnASealedServer_ThroughTheSealedUnlock()
    {
        var dek = Session.GetMasterDek();
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        (await Doors.ListAsync()).Should().ContainSingle(d => d.SlotType == ServerAutoUnlockService.SlotType && !d.Pending);
        (await Boot.GetMetaAsync(BootMetaKeys.ServerAutoUnlock)).Should().Be("1");

        var host = new SealedWritersTests.FakeSealedHost(Doors) { Usable = false };
        var restarted = Make(session: new SessionService(KeySlots), host: host, sealedNode: true);

        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);

        host.Unlocked.Should().ContainSingle();
        host.Unlocked[0].Dek.Should().Equal(dek);
        host.Unlocked[0].Door!.SlotType.Should().Be(ServerAutoUnlockService.SlotType);
        host.Fingerprints.Should().ContainSingle().Which.Fingerprint.Should().Be(DekFingerprint.Of(dek));
    }

    [Fact]
    public async Task AfterDisableARestartDoesNotOpenIt_AndTheRecordWentFirst()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();

        (await svc.DisableAsync()).Should().BeTrue();

        File.Exists(svc.RecordPath).Should().BeFalse();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse("the node created it, so the node removes it");
        (await ServerSlotsAsync()).Should().BeEmpty();
        (await Doors.ListAsync()).Should().NotContain(d => d.SlotType == ServerAutoUnlockService.SlotType);
        (await Boot.GetMetaAsync(BootMetaKeys.ServerAutoUnlock)).Should().Be("0");
        (await AuditActionsAsync()).Should().Equal("server_auto_unlock_enabled", "server_auto_unlock_disabled");
        var restarted = Make(session: new SessionService(KeySlots));
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.NotApplicable);
        (await svc.DisableAsync()).Should().BeFalse("nothing left to turn off");
    }

    [Fact]
    public async Task APartlyDoneDisable_RecordRemovedButSlotLeft_IsInert_AndTheNextStartRepairsIt()
    {
        var svc = Make();
        await svc.EnableAsync();
        File.Delete(svc.RecordPath);   // "remove the enable record first": the crash came right after

        var restarted = Make(session: new SessionService(KeySlots));
        (await restarted.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleSlot);
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired);

        (await ServerSlotsAsync()).Should().BeEmpty();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse();
    }

    // --- the operator's key file ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOperatorsKeyFileIsReadNeverWrittenMovedOrDeleted()
    {
        var path = OperatorKeyFile();
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var svc = Make(configuredKeyFile: path);

        await svc.EnableAsync();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse("no node-created file when the operator provides one");
        (await svc.GetStatusAsync()).Enabled.Should().BeTrue();

        var restarted = Make(session: new SessionService(KeySlots), configuredKeyFile: path);
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);

        await svc.DisableAsync();

        File.Exists(path).Should().BeTrue("the operator's file is never deleted");
        File.ReadAllBytes(path).Should().Equal(bytes);
        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
        _logs.Should().NotContain(l => l.Message.Contains(path), "the path of the operator's file is not logged");
    }

    [Fact]
    public async Task AConfiguredKeyPathThatIsTheNodeKeyPath_IsRefusedAndNeverWiped()
    {
        var probe = new ProbeKeyFiles();
        var svc = Make(keyFiles: probe);
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        OwnerOnlyFile.WriteNew(svc.NodeKeyFilePath, Encoding.ASCII.GetBytes(secret));
        var bytes = File.ReadAllBytes(svc.NodeKeyFilePath);
        var configured = Make(configuredKeyFile: svc.NodeKeyFilePath, keyFiles: probe);

        var act = async () => await configured.EnableAsync();

        (await act.Should().ThrowAsync<ServerAutoUnlockException>()).Which.Code.Should().Be("key_file_invalid");
        File.ReadAllBytes(svc.NodeKeyFilePath).Should().Equal(bytes);
        probe.Wipes.Should().Be(0);
        (await ServerSlotsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AConfiguredOperatorHardLinkToTheNodeKeyFile_IsRefused()
    {
        var node = Make();
        await node.EnableAsync();
        var operatorPath = Path.Combine(BootDir, "secrets", "hard-linked-key");
        Directory.CreateDirectory(Path.GetDirectoryName(operatorPath)!);
        TestLinks.CreateHardLink(node.NodeKeyFilePath, operatorPath);
        var configured = Make(configuredKeyFile: operatorPath);

        var act = async () => await configured.EnableAsync();

        (await act.Should().ThrowAsync<ServerAutoUnlockException>()).Which.Code.Should().Be("key_file_invalid");
    }

    [Fact]
    public async Task AConfiguredOperatorHardLinkToTheNodeKeyFile_IsNeverWipedDuringDisable()
    {
        var node = Make();
        await node.EnableAsync();
        var operatorPath = Path.Combine(BootDir, "secrets", "hard-linked-key");
        Directory.CreateDirectory(Path.GetDirectoryName(operatorPath)!);
        TestLinks.CreateHardLink(node.NodeKeyFilePath, operatorPath);
        var bytes = File.ReadAllBytes(operatorPath);
        var configured = Make(configuredKeyFile: operatorPath);

        await configured.DisableAsync();

        File.Exists(operatorPath).Should().BeTrue("the operator's hard link is never deleted");
        File.ReadAllBytes(operatorPath).Should().Equal(bytes, "node cleanup must not overwrite an operator's hard link");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("short")]
    public async Task AnUnusableOperatorsKeyFileRefusesTheEnable_AndCreatesNothing(string kind)
    {
        var path = kind switch
        {
            "missing" => Path.Combine(BootDir, "no-such-key"),
            "malformed" => OperatorKeyFile(new string('z', 64)),
            _ => OperatorKeyFile(new string('a', 63)),
        };
        var svc = Make(configuredKeyFile: path);

        var act = async () => await svc.EnableAsync();

        var ex = (await act.Should().ThrowAsync<ServerAutoUnlockException>()).Which;
        ex.Code.Should().Be("key_file_unusable");
        ex.Message.Should().NotContain(path, "no path in a message");
        (await ServerSlotsAsync()).Should().BeEmpty();
        File.Exists(svc.RecordPath).Should().BeFalse();
    }

    [UnixModeFact]
    public async Task AnOperatorsKeyFileThatOthersCanRead_IsRefused_OnLinuxAndMacOs()
    {
        var path = OperatorKeyFile();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var svc = Make(configuredKeyFile: path);

        var act = async () => await svc.EnableAsync();

        (await act.Should().ThrowAsync<ServerAutoUnlockException>()).Which.Message.Should().Contain("not_owner_only");
    }

    [WindowsAclFact]
    [SupportedOSPlatform("windows")]
    public async Task AnOperatorsKeyFileThatEveryAccountCanRead_IsRefused_OnWindows()
    {
        var shared = Path.Combine(BootDir, "shared");
        Directory.CreateDirectory(shared);
        WindowsAcl.MakeReadableByAllUsers(shared);
        var path = Path.Combine(shared, "key");
        File.WriteAllText(path, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        WindowsAcl.UsersCanRead(path).Should().BeTrue("the test built the situation");
        var svc = Make(configuredKeyFile: path);

        var act = async () => await svc.EnableAsync();

        (await act.Should().ThrowAsync<ServerAutoUnlockException>()).Which.Message.Should().Contain("not_owner_only");
    }

    // --- every prefix of the enable is inert ---------------------------------------------------------------------------------------

    private sealed class FailingCreateSlots(IKeySlotRepository inner) : IKeySlotRepository
    {
        public Task<List<MasterKeyStore>> GetAllAsync() => inner.GetAllAsync();
        public Task<int> CreateAsync(MasterKeyStore slot, System.Data.IDbTransaction? transaction = null) => throw new InvalidOperationException("disk full");
        public Task DeleteAsync(int slotId, System.Data.IDbTransaction? transaction = null) => inner.DeleteAsync(slotId, transaction);
        public Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv) => inner.UpdateSlotKeyAsync(slotId, encryptedDek, iv);
    }

    [Fact]
    public async Task AnEnableThatFailsAfterTheKeyFile_LeavesNoSlotNoRecordAndNoKeyFile()
    {
        var svc = Make(slots: new FailingCreateSlots(KeySlots));

        var act = async () => await svc.EnableAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse();
        File.Exists(svc.RecordPath).Should().BeFalse();
        (await ServerSlotsAsync()).Should().BeEmpty();
        (await Make().EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Off);
    }

    [Fact]
    public async Task AnEnableThatFailsBeforeTheRecord_LeavesAnInertSlot_TheNextStartRemoves_AndNeverUses()
    {
        var svc = Make();
        Directory.CreateDirectory(svc.RecordPath);   // the record cannot be written: a directory sits on its name

        var act = async () => await svc.EnableAsync();

        await act.Should().ThrowAsync<Exception>();
        (await ServerSlotsAsync()).Should().ContainSingle("the slot was written before the record");
        Directory.Delete(svc.RecordPath);

        var restarted = Make(session: new SessionService(KeySlots));
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired);
        (await ServerSlotsAsync()).Should().BeEmpty();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse();
        Session.Lock();
        new SessionService(KeySlots).IsUnlocked.Should().BeFalse();
    }

    // --- the four states at start and the restore cases ----------------------------------------------------------------------------

    private async Task<(ServerAutoUnlockService Svc, SealedWritersTests.FakeSealedHost Host)> EnabledSealedAsync(string? operatorFile = null)
    {
        var svc = Make(sealedNode: true, configuredKeyFile: operatorFile);
        await svc.EnableAsync();
        return (svc, new SealedWritersTests.FakeSealedHost(Doors) { Usable = false });
    }

    [Fact]
    public async Task AFreshRestore_SlotButNoRecord_StartsLocked_EvenWithTheOperatorsKeyFileStillMounted_AndNoUnlockIsTried()
    {
        var operatorFile = OperatorKeyFile();
        var (svc, host) = await EnabledSealedAsync(operatorFile);
        // The restored directory: the slot and its door came with the files, the enable record never does (spec 8.7.1).
        File.Delete(svc.RecordPath);
        var probe = new ProbeKeyFiles();
        var restored = Make(session: new SessionService(KeySlots), host: host, sealedNode: true, configuredKeyFile: operatorFile, keyFiles: probe);

        (await restored.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleSlot);
        (await restored.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired);

        host.Unlocked.Should().BeEmpty("a probe on TryUnlockWithDekAsync sees no call");
        probe.Calls.Should().Be(0, "no key file is read for a stale slot");
        (await Doors.ListAsync()).Should().NotContain(d => d.SlotType == ServerAutoUnlockService.SlotType, "the door is retired before any unlock is tried");
        (await Boot.GetMetaAsync(BootMetaKeys.ServerAutoUnlock)).Should().Be("0");
        (await ServerSlotsAsync()).Should().ContainSingle("the sealed database is closed: the row goes at the first open");
        File.Exists(operatorFile).Should().BeTrue("an operator's file is never removed");

        // The first password unlock opens the database: the open step deletes the slot and writes the audit entry.
        await new StaleServerSlotOpenStep(Make(sealedNode: true, configuredKeyFile: operatorFile)).RunAsync(true, CancellationToken.None);
        (await ServerSlotsAsync()).Should().BeEmpty();
        (await AuditActionsAsync()).Should().Contain("server_auto_unlock_retired_stale");
        var status = await restored.GetStatusAsync();
        status.Enabled.Should().BeFalse();
        status.Notice.Should().Contain("came from a backup or a copy");
        (await restored.GetStatusAsync()).Notice.Should().BeNull("the notice is shown once");

        // Turning it on again is the owner's explicit enable call.
        await Make(sealedNode: true, configuredKeyFile: operatorFile).EnableAsync();
        (await Make(configuredKeyFile: operatorFile).EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
    }

    [Fact]
    public async Task AFreshRestoreOnAPlainServer_DeletesTheSlotAtOnce_AndWritesTheAuditEntry()
    {
        var operatorFile = OperatorKeyFile();
        Gate.IsSealed = false;
        var svc = Make(configuredKeyFile: operatorFile);
        await svc.EnableAsync();
        File.Delete(svc.RecordPath);
        var restarted = Make(session: new SessionService(KeySlots), configuredKeyFile: operatorFile);

        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired);

        (await ServerSlotsAsync()).Should().BeEmpty();
        (await AuditActionsAsync()).Should().Contain("server_auto_unlock_retired_stale");
        new SessionService(KeySlots).IsUnlocked.Should().BeFalse();
        (await restarted.GetStatusAsync()).Notice.Should().NotBeNull();
    }

    [Fact]
    public async Task ARestoreWithTheNodeCreatedKeyFileAbsentOrPresent_AnotherMachine_APreUpdateSet_AndARecordDeletedByHand_AreAllStale()
    {
        foreach (var scenario in new[] { "key file absent", "key file present", "other machine", "record deleted by hand" })
        {
            var (svc, host) = await EnabledSealedAsync();
            File.Delete(svc.RecordPath);
            if (scenario == "key file absent") File.Delete(svc.NodeKeyFilePath);
            var restored = Make(session: new SessionService(KeySlots), host: host, sealedNode: true);

            (await restored.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleSlot, scenario);
            (await restored.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired, scenario);
            host.Unlocked.Should().BeEmpty(scenario);
            File.Exists(svc.NodeKeyFilePath).Should().BeFalse(scenario + ": the node-created file is wiped");

            await new StaleServerSlotOpenStep(Make(sealedNode: true)).RunAsync(true, CancellationToken.None);
            (await ServerSlotsAsync()).Should().BeEmpty(scenario);
        }
    }

    [Fact]
    public async Task AnInPlaceRestoreOfAnIdenticalSlot_OverTheServerThatEnabledIt_StaysArmed_ThePreservationOfTheOwnersEarlierChoice()
    {
        var (svc, host) = await EnabledSealedAsync();
        // The directory was restored over the live one: the database files and the boot store are replaced, the record and the key file stay.
        var slotBefore = (await ServerSlotsAsync()).Single();
        var restored = Make(session: new SessionService(KeySlots), host: host, sealedNode: true);

        (await restored.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
        (await restored.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);

        host.Unlocked.Should().ContainSingle();
        (await ServerSlotsAsync()).Single().SlotId.Should().Be(slotBefore.SlotId, "the restore created no slot, no record and no key file");
        File.Exists(svc.RecordPath).Should().BeTrue();
    }

    [Fact]
    public async Task AnInPlaceRestoreWhoseSlotDiffersFromTheOneTheRecordNames_IsStaleAndStartsLocked()
    {
        var (svc, host) = await EnabledSealedAsync();
        var oldRow = (await ServerSlotsAsync()).Single();

        // Since the backup the owner disabled it and enabled it again: the record names the new slot. The restore puts the old row back.
        await svc.DisableAsync();
        await svc.EnableAsync();
        using (var c = Factory.CreateConnection())
        {
            await c.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_type = 'server_auto_unlock'");
            await c.ExecuteAsync("INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at) VALUES ('server_auto_unlock', @w, @iv, 'old')",
                new { w = oldRow.EncryptedMasterDek, iv = oldRow.IV });
        }
        await Mirror.RewriteAsync();
        var restored = Make(session: new SessionService(KeySlots), host: host, sealedNode: true);

        (await restored.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleSlot);
        (await restored.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired);
        host.Unlocked.Should().BeEmpty();
    }

    [Fact]
    public async Task ARecordWithoutASlotIsRemoved_AndSoIsTheNodeCreatedKeyFile_ButNeverTheOperatorsFile()
    {
        var operatorFile = OperatorKeyFile();
        var svc = Make(configuredKeyFile: operatorFile);
        await svc.EnableAsync();
        await File.WriteAllTextAsync(svc.NodeKeyFilePath, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));   // a leftover of an earlier run
        using (var c = Factory.CreateConnection()) await c.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_type = 'server_auto_unlock'");
        var restarted = Make(session: new SessionService(KeySlots), configuredKeyFile: operatorFile);

        (await restarted.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleRecord);
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Repaired);

        File.Exists(svc.RecordPath).Should().BeFalse();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse();
        File.Exists(operatorFile).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnreadableOrForeignRecordCountsAsNoRecord()
    {
        var svc = Make();
        await svc.EnableAsync();
        foreach (var junk in new[] { "not json", "{}", "{\"v\":2,\"slot_sha256\":\"x\",\"dek_fp\":\"y\",\"enabled_at\":\"z\"}", "" })
        {
            await File.WriteAllTextAsync(svc.RecordPath, junk);
            (await Make().EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleSlot, junk);
        }
    }

    // --- armed, but the unlock does not happen: locked, and the state stays armed ----------------------------------------------------

    [Fact]
    public async Task AnArmedServerWhoseSecretDoesNotOpenTheSlot_StaysLockedAndArmed_WithAWarningThatNamesNoKeyAndNoPath()
    {
        var svc = Make();
        await svc.EnableAsync();
        await File.WriteAllTextAsync(svc.NodeKeyFilePath, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));   // another secret
        var restarted = Make(session: new SessionService(KeySlots));

        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.StayedLocked);

        (await restarted.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed, "a transient problem must not erase the owner's choice");
        File.Exists(svc.RecordPath).Should().BeTrue();
        (await ServerSlotsAsync()).Should().ContainSingle();
        _logs.Where(l => l.Level == LogLevel.Warning).Should().NotBeEmpty();
        _logs.Should().NotContain(l => l.Message.Contains(svc.NodeKeyFilePath) || l.Message.Contains(File.ReadAllText(svc.NodeKeyFilePath).Trim()));
        (await restarted.GetStatusAsync()).Enabled.Should().BeFalse("the key file and the slot do not agree");
    }

    [Fact]
    public async Task AnArmedServerWhoseKeyFileIsMissing_OrMalformed_StaysLocked()
    {
        var svc = Make();
        await svc.EnableAsync();
        var restarted = Make(session: new SessionService(KeySlots));

        File.Delete(svc.NodeKeyFilePath);
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.StayedLocked);
        (await restarted.GetStatusAsync()).Enabled.Should().BeFalse();

        await File.WriteAllTextAsync(svc.NodeKeyFilePath, "short");
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.StayedLocked);
        (await restarted.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
    }

    [Fact]
    public async Task AnArmedServerWhoseSlotWrapsAnotherKeyThanTheSentinel_StaysLocked()
    {
        var svc = Make();
        await svc.EnableAsync();
        // The database now belongs to another key (the sentinel changed), the slot and the record still name the old one.
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = @s", new { s = MasterKeyManager.ComputeSentinel(RandomNumberGenerator.GetBytes(32)) });
        var restarted = Make(session: new SessionService(KeySlots));

        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.StayedLocked);
    }

    /// <summary>BMB-312/308: a vault with no sentinel (NULL, or an empty BLOB) has nothing to disagree with the key, so the armed server opens it, as it always did for NULL.</summary>
    [Theory]
    [InlineData("NULL")]
    [InlineData("X''")]
    public async Task AnArmedServerWhoseVaultHasNoSentinel_OfEitherShape_StillUnlocks(string missing)
    {
        var svc = Make();
        await svc.EnableAsync();
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync($"UPDATE tbl_node_identity SET sentinel_value = {missing}");
        var restarted = Make(session: new SessionService(KeySlots));

        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
    }

    [Fact]
    public async Task AnArmedServerWhoseSealedUnlockRefuses_StaysLocked()
    {
        var (_, host) = await EnabledSealedAsync();
        host.Accept = false;
        var restarted = Make(session: new SessionService(KeySlots), host: host, sealedNode: true);

        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.StayedLocked);
        host.Fingerprints.Should().BeEmpty();
    }

    // --- rotation and reset ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARotationSwitchesTheCheckboxOff_SlotRecordAndNodeCreatedKeyFileGone_OnTheInitiatorAndOnAnAutoAcceptingPeer(bool initiator)
    {
        var operatorFile = OperatorKeyFile();
        var svc = Make(sealedNode: true, configuredKeyFile: operatorFile);
        await svc.EnableAsync();
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        (int Slot, byte[] W, byte[] Iv)? slot = null;
        if (initiator)
        {
            var user = (await KeySlots.GetAllAsync()).Where(s => s.SlotType == "user").OrderBy(s => s.SlotId).First();
            var kek = KeyDerivation.DeriveKek(AdminPassword, user.Salt!, user.ArgonMemory!.Value, user.ArgonIterations!.Value, user.ArgonParallelism!.Value);
            var (w, iv) = MasterKeyManager.WrapMasterDek(newDek, kek);
            slot = (user.SlotId, w, iv);
        }

        await BeeMemoryBank.Sync.DekRotation.DekRewrapper.RewrapAllAsync(Factory, Session, oldDek, newDek, 2, Guid.NewGuid().ToString(), initiator,
            slot?.Slot, slot?.W, slot?.Iv, sealedHooks: new SealedRotationHooks(Mirror, svc));

        (await ServerSlotsAsync()).Should().BeEmpty("the commit deleted the slot");
        File.Exists(svc.RecordPath).Should().BeFalse("the rotation hook removed the enable record");
        (await Doors.ListAsync()).Should().NotContain(d => d.SlotType == ServerAutoUnlockService.SlotType);
        File.Exists(operatorFile).Should().BeTrue("an operator's file is never touched");
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Off);
    }

    [Fact]
    public async Task ARotationRemovesTheNodeCreatedKeyFileToo()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        File.Exists(svc.NodeKeyFilePath).Should().BeTrue();

        await BeeMemoryBank.Sync.DekRotation.DekRewrapper.RewrapAllAsync(Factory, Session, Session.GetMasterDek(), RandomNumberGenerator.GetBytes(32), 2,
            Guid.NewGuid().ToString(), isInitiator: false, sealedHooks: new SealedRotationHooks(Mirror, svc));

        File.Exists(svc.NodeKeyFilePath).Should().BeFalse();
        File.Exists(svc.RecordPath).Should().BeFalse();
    }

    [Fact]
    public async Task ANodeResetRemovesTheRecordAndTheNodeCreatedKeyFile()
    {
        var svc = Make();
        await svc.EnableAsync();

        await new ServerAutoUnlockResetHook(svc).AfterVaultWipedAsync(CancellationToken.None);

        File.Exists(svc.RecordPath).Should().BeFalse();
        File.Exists(svc.NodeKeyFilePath).Should().BeFalse();
    }

    [Fact]
    public async Task TheOpenStepKeepsASlotTheRecordNames_AndHasOrderFifty()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        var step = new StaleServerSlotOpenStep(svc);

        step.Order.Should().Be(50);
        await step.RunAsync(true, CancellationToken.None);

        (await ServerSlotsAsync()).Should().ContainSingle("the slot is the one the enable record names");
        (await AuditActionsAsync()).Should().NotContain("server_auto_unlock_retired_stale");
    }

    [Fact]
    public async Task AnOsAutoUnlockSlotOnAServerKeepsWorkingAtStart()
    {
        // 11.2 "OS auto-unlock on a server": the route refuses to turn it on (Integration), but nothing is turned off by an upgrade.
        var dir = Path.Combine(BootDir, "os");
        Directory.CreateDirectory(dir);
        var secrets = new BeeMemoryBank.Infrastructure.Secrets.InMemoryUserSecretStore();
        var os = new BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService(KeySlots, Session, dir, secrets);
        await os.EnableAsync();
        var dek = Session.GetMasterDek();

        var restarted = new SessionService(KeySlots);
        var fresh = new BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService(KeySlots, restarted, dir, secrets);

        (await fresh.TryAutoUnlockAsync(new NodeIdentityRepository(Factory))).Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(dek);
    }

    [Fact]
    public async Task TheRegistrationCreatesNothingOnDisk_AndReadsNoKeyFile()
    {
        var probe = new ProbeKeyFiles();
        var services = new ServiceCollection()
            .AddSingleton<IKeySlotRepository>(KeySlots)
            .AddSingleton(Session)
            .AddSingleton<IServerKeyFileStore>(probe)
            .AddServerAutoUnlock(DataDir)
            .BuildServiceProvider();

        var svc = services.GetRequiredService<ServerAutoUnlockService>();

        svc.Role.Should().Be(HostRoles.FromEnvironment());
        probe.Calls.Should().Be(0);
        Directory.Exists(DataDir).Should().BeFalse("registering is not starting");
        services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().ContainSingle(h => h is ServerAutoUnlockStartup);
        services.GetServices<IVaultOpenStep>().Should().ContainSingle(s => s is StaleServerSlotOpenStep);
        await Task.CompletedTask;
    }
}
