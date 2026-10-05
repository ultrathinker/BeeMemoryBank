using System.Runtime.Versioning;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;
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

    [Fact]
    public async Task Enable_WithAStoreThatHasNoFile_SucceedsAndIsEnabled()
    {
        await Session.UnlockAsync("correctPassword");
        var store = new InMemoryUserSecretStore();
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir, store);

        var result = await svc.EnableAsync();

        result.Should().BeEmpty();
        File.Exists(svc.SecretFilePath).Should().BeFalse();
        (await svc.IsEnabledAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentEnable_LeavesOneSlotThatMatchesTheStoredSecret()
    {
        await Session.UnlockAsync("correctPassword");
        var store = new BlockingFirstWriteStore();
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir, store);

        var first = Task.Run(() => svc.EnableAsync());
        await store.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            secondEntered.SetResult();
            return await svc.EnableAsync();
        });
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var completed = await Task.WhenAny(store.SecondWriteEntered.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            completed.Should().NotBe(store.SecondWriteEntered.Task,
                "the second enable must stay outside the write path until the first one releases its mutation lock");
        }
        finally
        {
            store.ReleaseFirstWrite.SetResult();
        }
        await Task.WhenAll(first, second);

        (await _keySlotRepo.GetAllAsync()).Should().ContainSingle(slot => slot.SlotType == "os_auto_unlock");
        var restarted = new SessionService(_keySlotRepo);
        (await new OsAutoUnlockService(_keySlotRepo, restarted, _tempDataDir, store)
            .TryAutoUnlockAsync(_nodeRepo)).Should().BeTrue();
        var duplicate = () => _keySlotRepo.CreateAsync(new MasterKeyStore
        {
            SlotType = "os_auto_unlock",
            EncryptedMasterDek = new byte[] { 1 },
            IV = new byte[] { 2 },
            CreatedAt = DateTime.UtcNow
        });
        await duplicate.Should().ThrowAsync<Exception>("the repaired schema must reject a second slot");
    }

    [Fact]
    public async Task NoAutoUnlockSlot_IsDisabledAndCannotUnlock()
    {
        var store = new InMemoryUserSecretStore();
        var service = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir, store);

        (await service.IsEnabledAsync()).Should().BeFalse();
        (await service.TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse();
    }

    [Fact]
    public async Task OneMatchingAutoUnlockSlot_IsEnabledAndCanUnlock()
    {
        await Session.UnlockAsync("correctPassword");
        var store = new InMemoryUserSecretStore();
        var secret = SecureRandom.GetBytes(32);
        var slotId = await AddAutoUnlockSlotAsync(secret, DateTime.UtcNow);
        store.Write("os-auto-unlock", "default", secret);
        Array.Clear(secret);
        Session.Lock();

        var restarted = new SessionService(_keySlotRepo);
        var service = new OsAutoUnlockService(_keySlotRepo, restarted, _tempDataDir, store);
        (await service.IsEnabledAsync()).Should().BeTrue();
        (await service.TryAutoUnlockAsync(_nodeRepo)).Should().BeTrue();
        (await _keySlotRepo.GetAllAsync()).Should().ContainSingle(slot => slot.SlotId == slotId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateAutoUnlockSlots_KeepTheSlotMatchingTheStoredSecret(bool storedSecretMatchesOlderSlot)
    {
        await Session.UnlockAsync("correctPassword");
        var olderSecret = SecureRandom.GetBytes(32);
        var newerSecret = SecureRandom.GetBytes(32);
        var olderId = await AddAutoUnlockSlotAsync(olderSecret, DateTime.UtcNow.AddMinutes(-1));
        var newerId = await AddAutoUnlockSlotAsync(newerSecret, DateTime.UtcNow);
        var store = new InMemoryUserSecretStore();
        store.Write("os-auto-unlock", "default", storedSecretMatchesOlderSlot ? olderSecret : newerSecret);
        Array.Clear(olderSecret);
        Array.Clear(newerSecret);
        Session.Lock();

        var restarted = new SessionService(_keySlotRepo);
        var service = new OsAutoUnlockService(_keySlotRepo, restarted, _tempDataDir, store);
        (await service.TryAutoUnlockAsync(_nodeRepo)).Should().BeTrue();

        var expectedId = storedSecretMatchesOlderSlot ? olderId : newerId;
        (await _keySlotRepo.GetAllAsync()).Should().ContainSingle(slot =>
            slot.SlotType == "os_auto_unlock" && slot.SlotId == expectedId);
        var duplicate = () => _keySlotRepo.CreateAsync(new MasterKeyStore
        {
            SlotType = "os_auto_unlock",
            EncryptedMasterDek = new byte[] { 1 },
            IV = new byte[] { 2 },
            CreatedAt = DateTime.UtcNow
        });
        await duplicate.Should().ThrowAsync<Exception>("repair must install the partial unique index");
    }

    [Fact]
    public async Task MissingSecretForExistingSlot_DoesNotMintAReplacement()
    {
        await Session.UnlockAsync("correctPassword");
        var store = new InMemoryUserSecretStore();
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir, store);
        await svc.EnableAsync();
        var before = (await _keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "os_auto_unlock").SlotId;
        store.Delete("os-auto-unlock", "default");
        Session.Lock();

        (await svc.TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse();
        store.Read("os-auto-unlock", "default").Should().BeNull();
        (await _keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "os_auto_unlock").SlotId.Should().Be(before);
    }

    [Fact]
    public async Task StoreWriteFailure_RollsBackTheCreatedSlot()
    {
        await Session.UnlockAsync("correctPassword");
        var store = new InMemoryUserSecretStore
        {
            WriteFailure = new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "test")
        };
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir, store);

        var action = () => svc.EnableAsync();

        await action.Should().ThrowAsync<UserSecretStoreException>();
        (await _keySlotRepo.GetAllAsync()).Should().NotContain(s => s.SlotType == "os_auto_unlock");
    }

    [Fact]
    public async Task Disable_AttemptsSecretDeletion_WhenSecretReadIsMalformed()
    {
        var store = new MalformedReadStore();
        var svc = new OsAutoUnlockService(_keySlotRepo, Session, _tempDataDir, store);

        await svc.DisableAsync();

        store.DeleteCalled.Should().BeTrue();
    }

    private sealed class MalformedReadStore : IUserSecretStore
    {
        public bool IsSupported => true;
        public bool DeleteCalled { get; private set; }

        public byte[]? Read(string purpose, string account) =>
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Malformed, "malformed");

        public void Write(string purpose, string account, ReadOnlySpan<byte> value) { }

        public void Delete(string purpose, string account) => DeleteCalled = true;
    }

    private sealed class BlockingFirstWriteStore : IUserSecretStore
    {
        private readonly InMemoryUserSecretStore _inner = new();
        private int _writeCount;

        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSupported => true;

        public byte[]? Read(string purpose, string account) => _inner.Read(purpose, account);

        public void Write(string purpose, string account, ReadOnlySpan<byte> value)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                FirstWriteEntered.SetResult();
                ReleaseFirstWrite.Task.GetAwaiter().GetResult();
            }
            else
            {
                SecondWriteEntered.SetResult();
            }

            _inner.Write(purpose, account, value);
        }

        public void Delete(string purpose, string account) => _inner.Delete(purpose, account);
    }

    private async Task<int> AddAutoUnlockSlotAsync(byte[] secret, DateTime createdAt)
    {
        var masterDek = Session.GetMasterDek();
        try
        {
            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, secret);
            return await _keySlotRepo.CreateAsync(new MasterKeyStore
            {
                SlotType = "os_auto_unlock",
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                CreatedAt = createdAt
            });
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }
}
