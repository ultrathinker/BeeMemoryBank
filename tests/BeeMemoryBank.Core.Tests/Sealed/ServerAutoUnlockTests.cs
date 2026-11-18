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
        record.GetProperty("slot_sha256").GetString().Should().Be(BootDoorHash.OfSlot(slot.SlotId, slot.SlotType, slot.EncryptedMasterDek, slot.IV, slot.Salt, null, null, null,
            slot.DoorVersion, slot.DoorPublicKey));
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

    /// <summary>
    /// A sealed host that opens the database the way the real gate does: the open runs the open steps before the unlock returns. The
    /// steps are added after construction, so a step can belong to the very service whose start calls this host (as in the API, where
    /// <see cref="StaleServerSlotOpenStep"/> wraps the singleton <see cref="ServerAutoUnlockService"/>).
    /// </summary>
    private sealed class OpeningSealedHost(IBootDoorStore doors) : ISealedAutoUnlockHost
    {
        public List<IVaultOpenStep> Steps { get; } = [];
        public int StepsRun { get; private set; }
        public bool Usable { get; private set; }
        public bool IsSealed => true;
        public bool IsUsable => Usable;
        public async Task<IReadOnlyList<BootDoor>> ListDoorsAsync(CancellationToken ct = default) => await doors.ListAsync(ct);
        public async Task<bool> TryUnlockWithDekAsync(byte[] masterDek, BootDoor? door, CancellationToken ct = default)
        {
            foreach (var step in Steps.OrderBy(s => s.Order))
            {
                await step.RunAsync(true, ct);
                StepsRun++;
            }
            Usable = true;
            return true;
        }
        public Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task TheStartOfASealedServer_OpensThroughTheOpenSteps_IncludingItsOwnStaleSlotStep_WithoutWaitingForItself()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        var host = new OpeningSealedHost(Doors);
        var restarted = Make(session: new SessionService(KeySlots), host: host, sealedNode: true);
        // The same instance, as the API registers it: one ServerAutoUnlockService behind both the start step and the open step.
        host.Steps.Add(new StaleServerSlotOpenStep(restarted));

        var start = restarted.RunAtStartAsync(new NodeIdentityRepository(Factory));
        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(15)));

        finished.Should().BeSameAs(start,
            "2.10.1 held its lock across the open and its own open step waited for that lock: a sealed server with the checkbox on never started (BMB-374)");
        (await start).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
        host.StepsRun.Should().Be(1);
        (await ServerSlotsAsync()).Should().ContainSingle("the armed slot matches the enable record, so the open step leaves it alone");
        File.Exists(restarted.RecordPath).Should().BeTrue();
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
        public Task<bool> ReplaceDoorAsync(MasterKeyStore door, byte[]? expectedEncryptedMasterDek, System.Data.IDbTransaction? transaction = null) =>
            inner.ReplaceDoorAsync(door, expectedEncryptedMasterDek, transaction);
        public Task SetDekFingerprintIfUnknownAsync(int slotId, byte[] encryptedMasterDek, string dekFingerprint) =>
            inner.SetDekFingerprintIfUnknownAsync(slotId, encryptedMasterDek, dekFingerprint);
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

    /// <summary>A rotation through the real rewrap, as the initiator (the admin's door sealed for its password) or as a peer.</summary>
    private async Task RotateAsync(byte[] newDek, bool initiator, IServerAutoUnlockLocalState hook, IUnlockMaterialMirror? mirror = null)
    {
        var oldDek = Session.GetMasterDek();
        BeeMemoryBank.Crypto.Doors.Sealed? door = null;
        int? slotId = null;
        if (initiator)
        {
            var user = (await KeySlots.GetAllAsync()).Where(s => s.SlotType == "user").OrderBy(s => s.SlotId).First();
            var kek = KeyDerivation.DeriveKek(AdminPassword, user.Salt!, user.ArgonMemory!.Value, user.ArgonIterations!.Value, user.ArgonParallelism!.Value);
            door = BeeMemoryBank.Crypto.Doors.Seal(user.SlotType, kek, user.Salt, newDek);
            Array.Clear(kek);
            slotId = user.SlotId;
        }
        await BeeMemoryBank.Sync.DekRotation.DekRewrapper.RewrapAllAsync(Factory, Session, oldDek, (byte[])newDek.Clone(), 2, Guid.NewGuid().ToString(), initiator,
            slotId, door?.Box, door?.Iv, sealedHooks: new SealedRotationHooks(mirror ?? Mirror, hook), newSlotDoorPublicKey: door?.PublicKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARotationKeepsTheCheckboxOn_TheRecordFollowsTheResealedDoor_AndTheNextStartOpensWithTheNewKey(bool initiator)
    {
        var operatorFile = OperatorKeyFile();
        var svc = Make(sealedNode: true, configuredKeyFile: operatorFile);
        await svc.EnableAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newDek, initiator, svc);

        var slot = (await ServerSlotsAsync()).Should().ContainSingle("the server door is sealed to the new DEK, not deleted").Subject;
        slot.DoorVersion.Should().Be(2);
        slot.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        var evaluation = await svc.EvaluateAsync();
        evaluation.State.Should().Be(ServerAutoUnlockState.Armed, "the enable record names the re-sealed door and the new DEK");
        evaluation.Record!.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        evaluation.Record.NextSlotSha256.Should().BeNull("the record is settled after the commit");
        File.Exists(operatorFile).Should().BeTrue("an operator's file is never touched");

        var host = new SealedWritersTests.FakeSealedHost(Doors) { Usable = false };
        var restarted = Make(session: new SessionService(KeySlots), host: host, sealedNode: true, configuredKeyFile: operatorFile);
        (await restarted.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked,
            "a cold start after the rotation opens without a password");
        host.Unlocked.Should().ContainSingle().Which.Dek.Should().Equal(newDek);
    }

    [Fact]
    public async Task ARotationOnAPlainServer_TheNextStartOpensWithTheNewKey()
    {
        var svc = Make();
        await svc.EnableAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newDek, initiator: false, svc);

        var restarted = new SessionService(KeySlots);
        (await Make(session: restarted).RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
        restarted.GetMasterDek().Should().Equal(newDek);
    }

    /// <summary>Forwards the preparation but never settles: the state a crash right after the commit leaves.</summary>
    private sealed class CrashAfterCommit(IServerAutoUnlockLocalState inner) : IServerAutoUnlockLocalState
    {
        public Task DisableLocalAsync(CancellationToken ct = default) => inner.DisableLocalAsync(ct);
        public Task PrepareRotationAsync(ServerRotationDoor change, CancellationToken ct = default) => inner.PrepareRotationAsync(change, ct);
        public Task OnDekRotatedAsync(bool committed, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ACrashBetweenTheCommitAndTheSettle_LeavesTheServerArmed_AndTheNextUnlockSettlesTheRecord()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newDek, initiator: false, new CrashAfterCommit(svc));

        var before = await svc.EvaluateAsync();
        before.State.Should().Be(ServerAutoUnlockState.Armed, "the record names the old and the re-sealed door, the old and the new DEK");
        before.Record!.NextSlotSha256.Should().NotBeNull();
        var host = new SealedWritersTests.FakeSealedHost(Doors) { Usable = false };
        var restartedSession = new SessionService(KeySlots);
        (await Make(session: restartedSession, host: host, sealedNode: true).RunAtStartAsync(new NodeIdentityRepository(Factory)))
            .Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
        host.Unlocked.Should().ContainSingle().Which.Dek.Should().Equal(newDek);

        await svc.UpgradeAsync();

        var after = await svc.EvaluateAsync();
        after.State.Should().Be(ServerAutoUnlockState.Armed);
        after.Record!.NextSlotSha256.Should().BeNull("the post-unlock step settles a record that still names two doors");
        after.Record.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
    }

    /// <summary>
    /// The enable record cannot be written inside the rotation (P4 open question 5: a failing file write): only the first write fails, or every
    /// write fails. <c>settle</c> false: nothing after the commit runs (a crash right after it). Everything else is forwarded.
    /// </summary>
    private sealed class PrepareFails(IServerAutoUnlockLocalState inner, bool settle, bool everyTime) : IServerAutoUnlockLocalState
    {
        private int _calls;
        public Task DisableLocalAsync(CancellationToken ct = default) => inner.DisableLocalAsync(ct);
        public Task PrepareRotationAsync(ServerRotationDoor change, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1 || everyTime) throw new IOException("the enable record could not be written");
            return settle ? inner.PrepareRotationAsync(change, ct) : Task.CompletedTask;   // a later call comes after the commit: not in a crash
        }
        public Task OnDekRotatedAsync(bool committed, CancellationToken ct = default) => settle ? inner.OnDekRotatedAsync(committed, ct) : Task.CompletedTask;
    }

    [Theory]
    [InlineData(true, false, true)]    // the record write failed once, inside the rotation: written after the commit, the checkbox stays on
    [InlineData(false, false, false)]  // a crash right after the commit: nothing more is written, the checkbox is off at the next start
    [InlineData(true, true, false)]    // the record cannot be written at all: the checkbox is off at the next start
    public async Task ARotationWhoseRecordCouldNotBePrepared_IsSettledAfterTheCommit_AndTheServerNeverOpensWithAStaleRecord(bool settle, bool everyTime,
        bool staysOn)
    {
        // P4: PrepareRotationAsync runs inside the rotation transaction and writes a file; a failure is logged and the rotation goes on. P11 O4: the
        // settle after the commit could not settle a record that never named the re-sealed door, so the checkbox went off at the next start even
        // without a crash. The record is now written for the re-sealed door after the commit (the same step, once more) and then settled. Whatever
        // happens, the start never opens the vault with anything but the committed key, and the password still opens it.
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newDek, initiator: false, new PrepareFails(svc, settle, everyTime));

        Session.GetMasterDek().Should().Equal(newDek, "the rotation committed although the record could not be prepared");
        var host = new SealedWritersTests.FakeSealedHost(Doors) { Usable = false };
        var restarted = new SessionService(KeySlots);
        var outcome = await Make(session: restarted, host: host, sealedNode: true).RunAtStartAsync(new NodeIdentityRepository(Factory));
        if (staysOn)
        {
            outcome.Should().Be(ServerAutoUnlockStartOutcome.Unlocked, "the record names the re-sealed door and the new key");
            host.Unlocked.Should().ContainSingle().Which.Dek.Should().Equal(newDek, "only the committed key opens");
        }
        else
        {
            outcome.Should().Be(ServerAutoUnlockStartOutcome.Repaired, "the record names a door that is gone: the checkbox is off, the node closed");
            host.Unlocked.Should().BeEmpty();
        }

        var byPassword = new SessionService(KeySlots);
        (await byPassword.UnlockAsync(AdminPassword)).Should().BeTrue("the password opens the rotated vault");
        byPassword.GetMasterDek().Should().Equal(newDek);
    }

    /// <summary>Writes the pending doors and the wrappers before the commit and never rewrites: the boot store a crash right after the commit leaves.</summary>
    private sealed class NoRewriteMirror(IUnlockMaterialMirror inner) : IUnlockMaterialMirror
    {
        public Task RewriteAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) => inner.AddPendingDoorAsync(door, ct);
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => inner.RetireDoorAsync(kind, sourceId, ct);
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => inner.AddDbKeyWrapperAsync(masterDek, ct);
        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration, IReadOnlyList<byte[]> masterDeks,
            CancellationToken ct = default) => inner.WriteNewBootStoreAsync(bootPath, vault, databaseKey, dbkGeneration, masterDeks, ct);
    }

    /// <summary>A session as a restarted process has it: locked, judging a door and the sentinel through the database.</summary>
    private SessionService RestartedSealedSession() => new(KeySlots, new ServiceCollection()
        .AddSingleton<INodeIdentityRepository>(_ => new NodeIdentityRepository(Factory))
        .AddScoped<IUserRepository>(_ => new UserRepository(Factory))
        .AddScoped<IAgentRepository>(_ => new AgentRepository(Factory))
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task ACrashBetweenTheCommitAndTheBootRewrite_StillOpensASealedServer_ThroughTheDoorThatHoldsTheCommittedKey()
    {
        // the boot store keeps the old door (old key) and the pending re-sealed one (new key) and the record names both; the start
        // must try each until the sealed unlock (sentinel and door check) accepts one, not give up after the first.
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newDek, initiator: false, new CrashAfterCommit(svc), new NoRewriteMirror(Mirror));

        var doors = (await Doors.ListAsync()).Where(d => d.SlotType == ServerAutoUnlockService.SlotType).ToList();
        doors.Should().HaveCount(2, "the crash left the old door and the pending re-sealed door");
        doors.Should().ContainSingle(d => d.Pending);
        (await svc.GetStatusAsync()).Enabled.Should().BeTrue("the open database holds the re-sealed door, and the record names its key as the next one");

        var restarted = RestartedSealedSession();
        var host = new SealedAutoUnlockHost(new FakeGate(VaultState.Closed), Doors, (dek, door, ct) => restarted.TryUnlockWithDekAsync(dek, door, ct));
        (await Make(session: restarted, host: host, sealedNode: true).RunAtStartAsync(new NodeIdentityRepository(Factory)))
            .Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
        restarted.GetMasterDek().Should().Equal(newDek);
    }

    [Fact]
    public async Task ARotationThatRollsBack_LeavesTheRecordOnTheOldDoor()
    {
        var svc = Make();
        await svc.EnableAsync();
        var oldDek = Session.GetMasterDek();
        var before = (await svc.EvaluateAsync()).Record!;

        await svc.PrepareRotationAsync(new ServerRotationDoor(before.SlotSha256, new string('a', 64), new string('b', 64)));
        await svc.OnDekRotatedAsync(committed: false);

        var after = (await svc.EvaluateAsync()).Record!;
        after.SlotSha256.Should().Be(before.SlotSha256);
        after.DekFingerprint.Should().Be(DekFingerprint.Of(oldDek));
        after.NextSlotSha256.Should().BeNull();
        after.NextDekFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task AV1ServerDoor_IsStillRemovedByARotation_WithItsRecordAndTheNodeCreatedKeyFile()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        await DowngradeToV1Async(svc);
        await Mirror.RewriteAsync();
        File.Exists(svc.NodeKeyFilePath).Should().BeTrue();

        await BeeMemoryBank.Sync.DekRotation.DekRewrapper.RewrapAllAsync(Factory, Session, Session.GetMasterDek(), RandomNumberGenerator.GetBytes(32), 2,
            Guid.NewGuid().ToString(), isInitiator: false, sealedHooks: new SealedRotationHooks(Mirror, svc));

        (await ServerSlotsAsync()).Should().BeEmpty("a v1 door holds the old DEK and the rotation cannot re-seal it");
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
    public async Task TheOpenStepKeepsASlotTheRecordNames_AndRunsAfterTheMigrations()
    {
        var svc = Make(sealedNode: true);
        await svc.EnableAsync();
        var step = new StaleServerSlotOpenStep(svc);

        // Order 150: after the migrations step (100), because the step reads tbl_key_slot with the current columns (spec 11.2; the
        // first open of a database of an older release has the old ones until the migrations ran).
        step.Order.Should().Be(150);
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

    // ---- sealed doors (ADR 0012) --------------------------------------------------------------------------------------------------

    /// <summary>Turns the server's v2 slot back into the v1 wrap an earlier release wrote, and its record into the record of that release.</summary>
    private async Task<MasterKeyStore> DowngradeToV1Async(ServerAutoUnlockService svc)
    {
        var secret = Convert.FromHexString(File.ReadAllText(svc.NodeKeyFilePath).Trim());
        var dek = Session.GetMasterDek();
        var slot = (await ServerSlotsAsync()).Single();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, secret);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @id",
                new { wrapped, iv, id = slot.SlotId });
        var v1 = (await ServerSlotsAsync()).Single();
        File.WriteAllText(svc.RecordPath, System.Text.Json.JsonSerializer.Serialize(new ServerEnableRecord(1,
            BootDoorHash.OfSlot(v1.SlotId, v1.SlotType, v1.EncryptedMasterDek, v1.IV, null, null, null, null), DekFingerprint.Of(dek), DateTime.UtcNow.ToString("o"))));
        return v1;
    }

    [Fact]
    public async Task EnableWritesASealedDoor_AndTheRecordNamesItsV2Hash()
    {
        var svc = Make();
        await svc.EnableAsync();
        var slot = (await ServerSlotsAsync()).Single();
        slot.DoorVersion.Should().Be(2);
        slot.DoorPublicKey.Should().HaveCount(DoorBox.PublicKeySize);
        slot.EncryptedMasterDek.Should().HaveCount(DoorBox.BoxSize);
        slot.DekFingerprint.Should().Be(DekFingerprint.Of(Session.GetMasterDek()));
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
        File.ReadAllText(svc.RecordPath).Should().NotContain("next_slot_sha256", "only an upgrade in progress names a second door");
    }

    [Fact]
    public async Task AnArmedV1Slot_IsSealedAgainAfterAnUnlock_TheRecordFollows_AndTheNextStartStillUnlocks()
    {
        var dek = Session.GetMasterDek();
        var svc = Make();
        await svc.EnableAsync();
        var v1 = await DowngradeToV1Async(svc);
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed, "the record of an earlier release names the v1 slot");

        await svc.UpgradeAsync();

        var v2 = (await ServerSlotsAsync()).Single();
        v2.SlotId.Should().Be(v1.SlotId, "the door is replaced in place");
        v2.DoorVersion.Should().Be(2);
        v2.DekFingerprint.Should().Be(DekFingerprint.Of(dek));
        var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(svc.RecordPath)).RootElement;
        record.GetProperty("slot_sha256").GetString().Should().Be(
            BootDoorHash.OfSlot(v2.SlotId, v2.SlotType, v2.EncryptedMasterDek, v2.IV, null, null, null, null, v2.DoorVersion, v2.DoorPublicKey));
        record.TryGetProperty("next_slot_sha256", out _).Should().BeFalse("the upgrade finished");
        record.GetProperty("dek_fp").GetString().Should().Be(DekFingerprint.Of(dek));
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed, "the slot is never stale after its upgrade");

        await svc.UpgradeAsync();
        (await ServerSlotsAsync()).Single().EncryptedMasterDek.Should().Equal(v2.EncryptedMasterDek, "a v2 door is left alone");

        var restarted = new SessionService(KeySlots);
        (await Make(session: restarted).RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
        restarted.GetMasterDek().Should().Equal(dek);
    }

    [Fact]
    public async Task ACrashInTheMiddleOfTheUpgrade_LeavesARecordThatNamesBothDoors_AndTheServerStaysArmedWithEither()
    {
        var dek = Session.GetMasterDek();
        var svc = Make();
        await svc.EnableAsync();
        var v1 = await DowngradeToV1Async(svc);
        var secret = Convert.FromHexString(File.ReadAllText(svc.NodeKeyFilePath).Trim());
        var replacement = BeeMemoryBank.Crypto.Doors.WithDoor(v1, BeeMemoryBank.Crypto.Doors.Seal(ServerAutoUnlockService.SlotType, secret, null, dek));
        var oldHash = BootDoorHash.OfSlot(v1.SlotId, v1.SlotType, v1.EncryptedMasterDek, v1.IV, null, null, null, null);
        var newHash = BootDoorHash.OfSlot(replacement.SlotId, replacement.SlotType, replacement.EncryptedMasterDek, replacement.IV, null, null, null, null,
            replacement.DoorVersion, replacement.DoorPublicKey);
        File.WriteAllText(svc.RecordPath, System.Text.Json.JsonSerializer.Serialize(
            new ServerEnableRecord(1, oldHash, DekFingerprint.Of(dek), DateTime.UtcNow.ToString("o"), newHash)));

        // A crash after the record named both doors and before the slot changed: armed with the old door.
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
        await svc.RetireStaleSlotsInOpenDatabaseAsync();
        (await ServerSlotsAsync()).Should().ContainSingle("the open step keeps a slot the record names");

        // A crash after the slot changed and before the record named only the new door: armed with the new one, and a start unlocks.
        (await KeySlots.ReplaceDoorAsync(replacement, v1.EncryptedMasterDek)).Should().BeTrue();
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
        await svc.RetireStaleSlotsInOpenDatabaseAsync();
        (await ServerSlotsAsync()).Should().ContainSingle();
        var restarted = new SessionService(KeySlots);
        var restartedService = Make(session: restarted);
        (await restartedService.RunAtStartAsync(new NodeIdentityRepository(Factory))).Should().Be(ServerAutoUnlockStartOutcome.Unlocked);
        restarted.GetMasterDek().Should().Equal(dek);

        // the step after the unlock (IUnlockedDoorUpgrade) finds the door v2 already and settles the record on it.
        await restartedService.UpgradeAsync();
        var settled = (await restartedService.EvaluateAsync()).Record!;
        settled.SlotSha256.Should().Be(newHash, "the record names the door there is");
        settled.NextSlotSha256.Should().BeNull();
        settled.DekFingerprint.Should().Be(DekFingerprint.Of(dek));
        File.ReadAllText(svc.RecordPath).Should().NotContain("next_slot_sha256");
    }

    [Fact]
    public async Task OnAnOpenDatabase_ASlotThatNamesAnotherDekThanTheRecord_IsNotArmed_AsOnAClosedOne()
    {
        // the open-database path passed no fingerprint, so any DEK the slot named matched the record.
        var svc = Make();
        await svc.EnableAsync();
        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.Armed);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET dek_fp = @fp WHERE slot_type = 'server_auto_unlock'", new { fp = new string('a', 64) });

        (await svc.EvaluateAsync()).State.Should().Be(ServerAutoUnlockState.StaleSlot, "the slot names a DEK the record does not");
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
