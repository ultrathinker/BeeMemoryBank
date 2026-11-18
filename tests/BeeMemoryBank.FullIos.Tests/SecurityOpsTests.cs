using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The parity part 3 operations on a real vault: change master password on a plain and on a sealed
/// phone (old fails, new opens), the Debug slots (add a testing password, delete it, keep the login
/// slot), remove a device (revoke logged then stamped), the wipe's file list, and the recent events.
/// </summary>
public class SecurityOpsTests
{
    private const string Old = "Correct-Horse-9";
    private const string New = "New-Horse-10";

    [Fact]
    public async Task ChangeMaster_Plain_OldFails_NewOpens()
    {
        var (test, _) = await TestVault.CreateAsync("change-plain");
        await using var _2 = test;
        var security = new SecurityOps(test.Services);
        var vault = test.Vault;

        await security.ChangeMasterAsync(Old, New, "a new hint");

        vault.Lock();
        (await vault.UnlockAsync(Old)).Should().BeFalse();
        (await vault.UnlockAsync(New)).Should().BeTrue();
    }

    [Fact]
    public async Task ChangeMaster_Sealed_OldFails_NewOpens()
    {
        var dir = TestFolders.New("change-sealed");
        var start = PhoneVaultLifecycle.Begin(dir);
        var collection = new ServiceCollection()
            .AddFullNode(new FullNodePaths(dir))
            .AddPhoneVault(dir, start, encryptionOffered: true)
            .AddOpenWork()
            .AddSingleton<IPhoneEnvironment>(new FakeEnv());
        collection.AddSingleton<PeerHoldsHead>(_ => _ => Task.FromResult(true));
        var services = collection.BuildServiceProvider();
        await using var _2 = services;
        await FullNodeServices.PrepareAsync(services);

        var vault = new FullVault(services);
        await vault.CreateAsync("Test iPhone", Old, "A hint for the sealed vault");
        services.GetRequiredService<ISyncProtocolPolicy>().MarkActivated();
        var seal = services.GetRequiredService<SealMigrationService>();
        (await seal.PreflightAsync(new SealOptions())).Blockers.Should().BeEmpty();
        (await seal.RunAsync(new SealOptions())).Phase.Should().Be(SealProgress.Done);

        var security = new SecurityOps(services);
        await security.ChangeMasterAsync(Old, New, "a new sealed hint");

        var lifecycle = services.GetRequiredService<PhoneVaultLifecycle>();
        await lifecycle.OnBackgroundAsync();
        (await lifecycle.UnlockWithPasswordAsync(Old)).Should().BeFalse();
        (await lifecycle.UnlockWithPasswordAsync(New)).Should().BeTrue();
        start.Lease.Dispose();
    }

    [Fact]
    public async Task ChangeMaster_WrongCurrent_Throws()
    {
        var (test, _) = await TestVault.CreateAsync("change-wrong");
        await using var _2 = test;
        var security = new SecurityOps(test.Services);
        var act = () => security.ChangeMasterAsync("wrong-password", New, "hint");
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Slots_AddTestingPassword_DeleteIt_KeepLoginSlot()
    {
        var (test, _) = await TestVault.CreateAsync("slots");
        await using var _2 = test;
        var security = new SecurityOps(test.Services);

        var before = await security.SlotsAsync();
        // The slot the account signs in with is never deletable; a recovery slot may be.
        before.Where(s => s.SlotType is "user" or "password").Should().OnlyContain(s => !s.CanDelete);

        await security.AddDevSlotAsync("Dev-Pass-123");
        var after = await security.SlotsAsync();
        after.Count.Should().Be(before.Count + 1);
        // The testing password is an extra "user" slot no account points at, so it is deletable.
        var added = after.First(s => s.CanDelete && s.SlotType == "user");

        await security.RemoveSlotAsync(added.SlotId);
        (await security.SlotsAsync()).Count.Should().Be(before.Count);
    }

    [Fact]
    public async Task RevokePeer_LogsThenStamps()
    {
        var (test, _) = await TestVault.CreateAsync("revoke");
        await using var _2 = test;
        var security = new SecurityOps(test.Services);
        var peer = Guid.NewGuid();
        using (var scope = test.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new Core.Models.WhitelistEntry
            {
                NodeId = peer,
                DisplayName = "A computer",
                Status = "A",
                Ed25519PublicKey = new byte[32],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await security.RevokePeerAsync(peer);

        using var scope2 = test.Services.CreateScope();
        var active = await scope2.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        active.Should().NotContain(w => w.NodeId == peer);
        var events = await security.RecentEventsAsync(5);
        events.Should().Contain(e => e.EventType == "whitelist_revoke");
    }

    [Fact]
    public void WipeFiles_ListsDatabaseSidecarsAndMedia()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-wipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "media"));
        File.WriteAllText(Path.Combine(dir, "beememorybank.db"), "x");
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-wal"), "x");
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-shm"), "x");
        File.WriteAllText(Path.Combine(dir, "beememorybank.boot.db"), "x");
        File.WriteAllText(Path.Combine(dir, "media", "m.bin"), "x");

        var files = SecurityOps.WipeFiles(dir);
        files.Should().Contain(f => f.EndsWith("beememorybank.db"));
        files.Should().Contain(f => f.EndsWith("beememorybank.db-wal"));
        files.Should().Contain(f => f.EndsWith("beememorybank.db-shm"));
        files.Should().Contain(f => f.EndsWith("beememorybank.boot.db"));
        files.Should().Contain(f => f.EndsWith("m.bin"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task RecentEvents_ShowsTheWrite()
    {
        var (test, _) = await TestVault.CreateAsync("events");
        await using var _2 = test;
        var security = new SecurityOps(test.Services);
        await test.Notes.SaveAsync(null, "Event note", "/", "x");
        var events = await security.RecentEventsAsync(5);
        events.Should().Contain(e => e.EventType == "article_create");
    }

    private sealed class FakeEnv : IPhoneEnvironment
    {
        public bool IsForeground { get; set; } = true;
        public int? BatteryPercent { get; set; } = 80;
        public bool IsCharging { get; set; }
        public bool PeerHoldsHead { get; set; } = true;
        public long? Free { get; set; }
        public long? FreeBytes(string path) => Free;
    }
}
