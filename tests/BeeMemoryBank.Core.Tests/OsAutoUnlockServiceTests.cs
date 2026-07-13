using System.Runtime.Versioning;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Verifies the opt-in DPAPI-based auto-unlock slot: enabling it lets a fresh
/// <see cref="SessionService"/> instance (simulating a process restart) recover the exact same
/// master DEK without a password, the DPAPI-protected secret file is not recoverable as plaintext,
/// and disabling it genuinely prevents auto-unlock afterward. Windows-only (DPAPI).
/// </summary>
[SupportedOSPlatform("windows")]
public class OsAutoUnlockServiceTests : TestFixture
{
    private KeySlotRepository _keySlotRepo = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private string _tempDataDir = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _keySlotRepo = new KeySlotRepository(Factory);
        _nodeRepo = new NodeIdentityRepository(Factory);
        await InitService.InitializeAsync("admin", "TestNode", "correctPassword");

        _tempDataDir = Path.Combine(Path.GetTempPath(), "bmb-autounlock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
    }

    public override Task DisposeAsync()
    {
        try { Directory.Delete(_tempDataDir, recursive: true); } catch { }
        return base.DisposeAsync();
    }

    [Fact]
    public async Task EnableThenAutoUnlock_OnFreshSessionInstance_RecoversSameMasterDek()
    {
        if (!OperatingSystem.IsWindows()) return;

        await Session.UnlockAsync("correctPassword");
        var originalDek = Session.GetMasterDek();

        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir);
        await svc.EnableAsync();

        // Simulate a process restart: a brand-new SessionService (locked) + a new
        // OsAutoUnlockService instance pointed at the same on-disk secret file and DB.
        var freshSession = new SessionService(_keySlotRepo);
        freshSession.IsUnlocked.Should().BeFalse();
        var freshSvc = new OsAutoUnlockService(_keySlotRepo, freshSession, _tempDataDir);

        var unlocked = await freshSvc.TryAutoUnlockAsync(_nodeRepo);

        unlocked.Should().BeTrue();
        freshSession.IsUnlocked.Should().BeTrue();
        freshSession.GetMasterDek().Should().Equal(originalDek);
    }

    [Fact]
    public async Task Enable_DoesNotWriteRawSecretAsPlaintextToDisk()
    {
        if (!OperatingSystem.IsWindows()) return;

        await Session.UnlockAsync("correctPassword");
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir);

        var dpapiBytes = await svc.EnableAsync();

        var onDiskBytes = await File.ReadAllBytesAsync(svc.SecretFilePath);
        onDiskBytes.Should().Equal(dpapiBytes, "the file must contain exactly the DPAPI-protected bytes");

        // A DPAPI blob is structurally different from a raw 32-byte secret: it's longer (DPAPI
        // adds its own header/HMAC overhead) and its content is not directly usable as a KEK.
        onDiskBytes.Length.Should().BeGreaterThan(32,
            "DPAPI-protected output must carry more than just the raw 32-byte secret");
    }

    [Fact]
    public async Task Disable_RemovesSlotAndSecretFile_AndPreventsFurtherAutoUnlock()
    {
        if (!OperatingSystem.IsWindows()) return;

        await Session.UnlockAsync("correctPassword");
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir);
        await svc.EnableAsync();

        (await svc.IsEnabledAsync()).Should().BeTrue();
        File.Exists(svc.SecretFilePath).Should().BeTrue();

        var disabled = await svc.DisableAsync();
        disabled.Should().BeTrue();

        (await svc.IsEnabledAsync()).Should().BeFalse();
        File.Exists(svc.SecretFilePath).Should().BeFalse();

        // A fresh locked session must NOT be auto-unlockable anymore.
        var freshSession = new SessionService(_keySlotRepo);
        var freshSvc = new OsAutoUnlockService(_keySlotRepo, freshSession, _tempDataDir);
        var unlocked = await freshSvc.TryAutoUnlockAsync(_nodeRepo);

        unlocked.Should().BeFalse();
        freshSession.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task OsAutoUnlockSlot_IsNeverTriedByPasswordUnlock()
    {
        if (!OperatingSystem.IsWindows()) return;

        await Session.UnlockAsync("correctPassword");
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir);
        await svc.EnableAsync();
        Session.Lock();

        // Password unlock must still work exactly as before — the os_auto_unlock slot (no Salt/
        // ArgonMemory) must be transparently skipped by the password-based unlock path.
        var result = await Session.UnlockAsync("correctPassword");
        result.Should().BeTrue();
        Session.IsUnlocked.Should().BeTrue();
    }
}
