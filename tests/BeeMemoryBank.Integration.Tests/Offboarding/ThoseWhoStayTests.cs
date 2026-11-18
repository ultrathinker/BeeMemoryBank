using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests.Offboarding;

/// <summary>
/// Those who stay notice nothing (release 2.12.0, plan 5): after a removal closes - its master key changed on every full node - every remaining
/// superadmin opens every node with the password they always had, their own recovery key still opens the node, and the doors that open a node without
/// a person (an auto-unlock agent, the desktop's and the server's auto-unlock door) open the NEW key. The leaver's own recovery key is the contrast.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class ThoseWhoStayTests
{
    private static async Task<string> IssueRecoveryKeyAsync(BmbWebApplicationFactory node, int userId)
    {
        using var client = OffboardingStand.ClientAs(node, userId);
        var response = await client.PostAsync("/api/keys/add-recovery", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryKey").GetString()!;
    }

    private static byte[] CurrentDek(BmbWebApplicationFactory node) => node.Services.GetRequiredService<SessionService>().GetMasterDek();

    /// <summary>The node is closed (as after a restart) and opened again with <paramref name="secret"/>; true when it opened.</summary>
    private static async Task<bool> ReopensWithAsync(BmbWebApplicationFactory node, string secret)
    {
        var session = node.Services.GetRequiredService<SessionService>();
        session.Lock();
        return await session.UnlockAsync(secret);
    }

    [Fact]
    public async Task After_the_close_every_remaining_superadmin_and_every_door_without_a_person_opens_the_new_key()
    {
        await using var stand = await OffboardingStand.CreateAsync(othersDevice: true);

        // What those who stay hold before the removal: the administrator's recovery key and auto-unlock agent, and the node's own auto-unlock door
        // (the desktop's or the server's: a door keyed by a secret on the machine, not by a person). The leaver holds a recovery key of their own.
        var adminRecovery = await IssueRecoveryKeyAsync(stand.Accounts, stand.AdminId);
        var leaverRecovery = await IssueRecoveryKeyAsync(stand.Accounts, stand.LeaverId);
        string agentKey;
        using (var admin = OffboardingStand.ClientAs(stand.Accounts, stand.AdminId))
        {
            var created = await admin.PostAsJsonAsync("/api/agents", new { name = "Admin's helper", description = "assistant" });
            created.EnsureSuccessStatusCode();
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("canAutoUnlock").GetBoolean().Should().BeTrue("a superadmin's agent can wake a closed node");
            agentKey = body.GetProperty("apiKey").GetString()!;
        }
        var machineSecret = SecureRandom.GetBytes(32);
        int machineSlotId;
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var dek = CurrentDek(stand.Accounts);
            try
            {
                var slot = Doors.NewSecretSlot(KeySlotTypes.OsAutoUnlock, machineSecret, dek);
                machineSlotId = await SlotDoors.CreateSlotAsync(sp.GetRequiredService<IKeySlotRepository>(), sp.GetRequiredService<IDbConnectionFactory>(),
                    sp.GetService<IUnlockMaterialMirror>(), slot, dek);
            }
            finally { Array.Clear(dek); }
        }
        var oldFp = DekFingerprint.Of(CurrentDek(stand.Accounts));

        // The removal runs to its end; the administrator's phone applies the removal's rotation by itself.
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        var view = await stand.ViewAsync(id);
        for (var round = 0; round < 20 && view.Open; round++)
        {
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.OthersDevice!);
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            view = await stand.ViewAsync(id);
            if (view.Open) await Task.Delay(250);
        }
        view.Open.Should().BeFalse();
        var newDek = CurrentDek(stand.Accounts);
        DekFingerprint.Of(newDek).Should().NotBe(oldFp, "the removal changed the master key");

        // The password they always had: a sign-in, and the opening of each node after a restart.
        using (var anonymous = stand.Accounts.CreateClient())
            (await anonymous.PostAsJsonAsync("/api/session/login", new { username = "admin", password = OffboardingStand.AdminPassword }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReopensWithAsync(stand.Accounts, OffboardingStand.AdminPassword)).Should().BeTrue("the accounts node");
        (await ReopensWithAsync(stand.OthersDevice!, OffboardingStand.AdminPassword)).Should().BeTrue("the administrator's other device");
        DekFingerprint.Of(CurrentDek(stand.OthersDevice!)).Should().Be(DekFingerprint.Of(newDek), "the device opened the new key");

        // Their recovery key opens the node; the leaver's does not.
        (await ReopensWithAsync(stand.Accounts, adminRecovery)).Should().BeTrue("a recovery key of somebody who stays survives the rotation");
        (await ReopensWithAsync(stand.Accounts, leaverRecovery)).Should().BeFalse("the leaver's recovery key went at the lock-out");
        (await ReopensWithAsync(stand.Accounts, OffboardingStand.AdminPassword)).Should().BeTrue();

        // The doors without a person open the NEW key: the auto-unlock agent and the machine's auto-unlock door.
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var agent = await sp.GetRequiredService<IAgentRepository>().GetByKeyHashAsync(AgentKeyHelper.ComputeKeyHash(agentKey));
            agent.Should().NotBeNull("the administrator's agent stays");
            Doors.OpenAgent(agent!, agentKey).Should().Equal(newDek, "the agent wakes a closed node with the new key");
            var machineSlot = (await sp.GetRequiredService<IKeySlotRepository>().GetAllAsync()).Single(s => s.SlotId == machineSlotId);
            Doors.OpenSlot(machineSlot, machineSecret).Should().Equal(newDek, "the machine's auto-unlock door was sealed to the new key");
        }
        Array.Clear(newDek);
    }

    [Fact]
    public async Task A_server_that_starts_unlocked_and_a_v1_straggler_door_still_open_the_node_after_the_close()
    {
        // Review weak tests: the server's "start unlocked after a restart" (the real ServerAutoUnlockService, its slot and its enable record) and a
        // superadmin who has not signed in since the update (a v1 door, which the rotation cannot re-seal) both open the NEW key after the close.
        await using var stand = await OffboardingStand.CreateAsync(serverAccounts: true);
        var server = stand.Accounts.Services.GetRequiredService<BeeMemoryBank.Infrastructure.AutoUnlock.ServerAutoUnlockService>();
        await server.EnableAsync();
        using (var scope = stand.Accounts.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("olga", "Olga", "Olga-Password-1", UserRoles.Superadmin, passwordHint: "her hint");
        await OffboardingStand.MakeDoorV1Async(stand.Accounts, "olga", "Olga-Password-1");
        var oldFp = DekFingerprint.Of(CurrentDek(stand.Accounts));

        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        await stand.KeepOpenSuperadminsAsync(id);   // Olga: a superadmin is decided one by one
        (await stand.ViewAsync(id)).Open.Should().BeFalse();
        var newFp = DekFingerprint.Of(CurrentDek(stand.Accounts));
        newFp.Should().NotBe(oldFp);

        // A restart of the server: closed, then the start step opens it from the key file and the enable record.
        var session = stand.Accounts.Services.GetRequiredService<SessionService>();
        session.Lock();
        using (var scope = stand.Accounts.Services.CreateScope())
            (await server.RunAtStartAsync(scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>()))
                .Should().Be(BeeMemoryBank.Infrastructure.AutoUnlock.ServerAutoUnlockStartOutcome.Unlocked);
        DekFingerprint.Of(CurrentDek(stand.Accounts)).Should().Be(newFp, "the server opened itself with the new key");

        // The straggler's old door reaches the new key (the bridge), with the password Olga always had.
        (await ReopensWithAsync(stand.Accounts, "Olga-Password-1")).Should().BeTrue();
        DekFingerprint.Of(CurrentDek(stand.Accounts)).Should().Be(newFp);
    }
}
