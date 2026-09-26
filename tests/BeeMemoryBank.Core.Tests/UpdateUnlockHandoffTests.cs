using System.Runtime.Versioning;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The one-shot handoff that keeps the vault open across a desktop update restart: it must
/// reopen a fresh (restarted) session with the same DEK, be gone after the first start whether
/// used or not, and refuse anything stale, foreign or tampered. Windows-only (DPAPI).
/// </summary>
[SupportedOSPlatform("windows")]
public class UpdateUnlockHandoffTests : TestFixture
{
    private KeySlotRepository _keySlotRepo = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private string _dataDir = null!;
    private DateTime _now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _keySlotRepo = new KeySlotRepository(Factory);
        _nodeRepo = new NodeIdentityRepository(Factory);
        await InitService.InitializeAsync("admin", "TestNode", "correctPassword");

        _dataDir = Path.Combine(Path.GetTempPath(), "bmb-handoff-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public override Task DisposeAsync()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        return base.DisposeAsync();
    }

    private UpdateUnlockHandoff For(SessionService session) => new(session, _dataDir, () => _now);

    [Fact]
    public async Task WrittenHandoff_UnlocksTheRestartedSession_WithTheSameDek_AndIsGone()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Session.UnlockAsync("correctPassword");
        var dek = Session.GetMasterDek();

        (await For(Session).WriteAsync()).Should().BeTrue();

        _now = _now.AddMinutes(2);
        var restarted = new SessionService(_keySlotRepo);
        var handoff = For(restarted);
        (await handoff.TryConsumeAsync(_nodeRepo)).Should().BeTrue();

        restarted.IsUnlocked.Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(dek);
        File.Exists(handoff.FilePath).Should().BeFalse("a handoff is used once");
    }

    [Fact]
    public async Task SecondStart_AfterTheHandoffWasUsed_StaysLocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Session.UnlockAsync("correctPassword");
        await For(Session).WriteAsync();
        (await For(new SessionService(_keySlotRepo)).TryConsumeAsync(_nodeRepo)).Should().BeTrue();

        var secondStart = new SessionService(_keySlotRepo);
        (await For(secondStart).TryConsumeAsync(_nodeRepo)).Should().BeFalse();
        secondStart.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredHandoff_IsRefused_AndRemoved()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Session.UnlockAsync("correctPassword");
        await For(Session).WriteAsync();

        _now = _now + UpdateUnlockHandoff.Lifetime + TimeSpan.FromSeconds(1);
        var restarted = new SessionService(_keySlotRepo);
        var handoff = For(restarted);
        (await handoff.TryConsumeAsync(_nodeRepo)).Should().BeFalse();

        restarted.IsUnlocked.Should().BeFalse();
        File.Exists(handoff.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task ClockThatJumpedBackwards_DoesNotStretchTheWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Session.UnlockAsync("correctPassword");
        await For(Session).WriteAsync();

        _now = _now.AddHours(-3);
        var restarted = new SessionService(_keySlotRepo);
        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeFalse();
        restarted.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task LockedSession_WritesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handoff = For(Session);

        (await handoff.WriteAsync()).Should().BeFalse();
        File.Exists(handoff.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task TamperedFile_IsRefused_AndRemoved()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Session.UnlockAsync("correctPassword");
        var writer = For(Session);
        await writer.WriteAsync();
        var bytes = await File.ReadAllBytesAsync(writer.FilePath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(writer.FilePath, bytes);

        var restarted = new SessionService(_keySlotRepo);
        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeFalse();
        restarted.IsUnlocked.Should().BeFalse();
        File.Exists(writer.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task HandoffFile_DoesNotContainTheDekInTheClear()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Session.UnlockAsync("correctPassword");
        var dek = Session.GetMasterDek();
        var handoff = For(Session);
        await handoff.WriteAsync();

        var onDisk = await File.ReadAllBytesAsync(handoff.FilePath);
        onDisk.AsSpan().IndexOf(dek).Should().Be(-1);
    }
}
