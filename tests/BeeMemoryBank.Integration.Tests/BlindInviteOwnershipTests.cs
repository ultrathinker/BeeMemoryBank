using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

[Collection(BlindInviteCollection.Name)]
public sealed class BlindInviteOwnershipTests(BlindInviteWorld world)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static (Guid NodeId, byte[] Seed, byte[] PublicKey, byte[] BackupKey) NewDevice()
    {
        var (pk, seed) = Ed25519Signer.GenerateKeyPair();
        return (BlindNodeId.NewId(), seed, pk, RandomNumberGenerator.GetBytes(32));
    }

    private async Task<(string InviteId, BlindInvitation Invitation, JsonElement Body)> IssueAsync(object request)
    {
        var resp = await world.Hub.Admin.PostAsJsonAsync("/api/blind-nodes/invites", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        BlindInvitation.TryParse(body.GetProperty("text").GetString()!, out var inv).Should().BeTrue();
        return (body.GetProperty("inviteId").GetString()!, inv!, body);
    }

    private async Task<HttpResponseMessage> RedeemAsync(BlindInvitation inv, (Guid NodeId, byte[] Seed, byte[] PublicKey, byte[] BackupKey) dev, string name)
    {
        var req = new BlindInviteRedeemRequest(inv.InviteId, inv.Secret, dev.NodeId, dev.PublicKey, name, "android", "2.12.0", 4, dev.BackupKey, []);
        req = req with
        {
            Proof = Ed25519Signer.Sign(dev.Seed, BlindInviteProofs.RedeemMessage(inv.InviteId, inv.HubNodeId, dev.NodeId, dev.PublicKey, dev.BackupKey))
        };
        using var content = new ByteArrayContent(req.ToJson());
        content.Headers.ContentType = new("application/json");
        return await world.Public.PostAsync("/api/sync/invite/redeem", content);
    }

    [Fact]
    public async Task Invite_DefaultsToSharedOwnership_OnConfirmAndWhitelistAdd()
    {
        await world.ResetAsync();
        using var scope0 = world.Hub.Services.CreateScope();
        var head = await scope0.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();

        var issued = await IssueAsync(new
        {
            label = "VPS Backup",
            sourceName = "Alex",
            lifetimeMinutes = 30,
            address = world.Hub.Origin,
            trust = "auto"
        });
        var list = await world.Hub.Admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/invites/", Web);
        var item = list.EnumerateArray().First(x => x.GetProperty("inviteId").GetString() == issued.InviteId);
        item.GetProperty("ownership").GetString().Should().Be("shared");
        item.GetProperty("ownerLabel").ValueKind.Should().Be(JsonValueKind.Null);

        var dev = NewDevice();
        (await RedeemAsync(issued.Invitation, dev, "Shared Relay")).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var confirm = await world.Hub.Admin.PostAsJsonAsync($"/api/blind-nodes/invites/{issued.InviteId}/confirm", new { password = BlindInviteWorld.Password });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        using var scope = world.Hub.Services.CreateScope();
        var wl = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(dev.NodeId);
        wl.Should().NotBeNull();
        wl!.Ownership.Should().Be(OwnershipKinds.Shared);
        wl.OwnerPersonId.Should().BeNull();
        wl.OwnerLabel.Should().BeNull();
        wl.NeedsReview.Should().BeFalse();

        var events = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(head);
        var addEvt = events.FirstOrDefault(e => e.EventType == EventTypes.WhitelistAdd && e.Payload.Contains(dev.NodeId.ToString(), StringComparison.OrdinalIgnoreCase));
        addEvt.Should().NotBeNull();
        var payload = JsonSerializer.Deserialize<WhitelistAddPayload>(addEvt!.Payload)!;
        payload.Ownership.Should().Be(OwnershipKinds.Shared);
        payload.OwnerPersonId.Should().BeNull();
    }

    [Fact]
    public async Task Invite_PersonalWithUser_PropagatesOwnerPersonIdAndLabel()
    {
        await world.ResetAsync();

        var users = await world.Hub.Admin.GetFromJsonAsync<JsonElement>("/api/users", Web);
        var firstUser = users.EnumerateArray().First();
        var personId = firstUser.GetProperty("personId").GetGuid();
        personId.Should().NotBe(Guid.Empty);
        var expectedLabel = firstUser.GetProperty("displayName").GetString();
        if (string.IsNullOrWhiteSpace(expectedLabel))
            expectedLabel = firstUser.GetProperty("username").GetString();

        var issued = await IssueAsync(new
        {
            label = "Owner Phone",
            sourceName = "Alex",
            lifetimeMinutes = 30,
            address = world.Hub.Origin,
            trust = "auto",
            ownership = "personal",
            ownerPersonId = personId
        });
        var list = await world.Hub.Admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/invites/", Web);
        var item = list.EnumerateArray().First(x => x.GetProperty("inviteId").GetString() == issued.InviteId);
        item.GetProperty("ownership").GetString().Should().Be("personal");
        item.GetProperty("ownerLabel").GetString().Should().Be(expectedLabel);

        var dev = NewDevice();
        (await RedeemAsync(issued.Invitation, dev, "Owner Pixel")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var confirm = await world.Hub.Admin.PostAsJsonAsync($"/api/blind-nodes/invites/{issued.InviteId}/confirm", new { password = BlindInviteWorld.Password });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        using var scope = world.Hub.Services.CreateScope();
        var wl = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(dev.NodeId);
        wl.Should().NotBeNull();
        wl!.Ownership.Should().Be(OwnershipKinds.Personal);
        wl.OwnerPersonId.Should().Be(personId);
        wl.OwnerLabel.Should().Be(expectedLabel);
        wl.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Invite_PersonalWithoutOwnerPersonId_ReturnsBadRequest()
    {
        await world.ResetAsync();
        var resp = await world.Hub.Admin.PostAsJsonAsync("/api/blind-nodes/invites", new
        {
            label = "Broken Personal",
            sourceName = "Alex",
            lifetimeMinutes = 30,
            address = world.Hub.Origin,
            trust = "auto",
            ownership = "personal"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
