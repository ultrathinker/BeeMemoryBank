using System.Net.Http.Json;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Strong box, cleanup and their triggers (plan 6.3, 6.5, 6.6). The host is a PC with ~1.8 GiB free,
/// so strong boxes use the 512 MiB preset and the tests stay reasonably quick.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class RecoveryBoxLifecycleTests : IAsyncLifetime
{
    private const string Password = "RecoveryPass1";
    private const long FreeBytes = 1_900L << 20;

    private RecoveryFactory _factory = new(RecoveryHostKind.Pc, FreeBytes);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        // Unlocked directly, not through /login: the login trigger would start a build of its own
        // in the background and race the test's explicit ones.
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // --- policy -----------------------------------------------------------------------------

    [Theory]
    [InlineData(RecoveryHostKind.Pc, 8L << 30, "s1024t4")]
    [InlineData(RecoveryHostKind.Pc, 1_900L << 20, "s512t6")]
    [InlineData(RecoveryHostKind.Pc, 1L << 30, null)]
    [InlineData(RecoveryHostKind.Hub, 4L << 30, "s1024t4")]
    [InlineData(RecoveryHostKind.Hub, 3L << 30, "s512t6")]
    [InlineData(RecoveryHostKind.Hub, 1L << 30, null)]
    [InlineData(RecoveryHostKind.Phone, 16L << 30, null)]
    public void Policy_PresetByHostAndMemory(RecoveryHostKind host, long available, string? expected)
    {
        StrongBoxPolicy.ChoosePreset(host, available).Should().Be(expected);
    }

    // --- strong box -------------------------------------------------------------------------

    [Fact]
    public async Task Login_WithABlindNodePaired_BuildsAStrongBoxThatOpensToTheCurrentKey()
    {
        await AddPeerAsync(BlindNodeId.NewId(), "Blind");

        await Triggers().OnSuperadminLogin(await AdminAsync(), Password);

        var box = (await OwnStrongBoxesAsync()).Should().ContainSingle().Subject;
        box.KdfPreset.Should().Be("s512t6");
        box.DekFingerprint.Should().Be(CurrentFingerprint());
        var opened = await HeavyDerivationQueue.RunAsync(() =>
            RecoveryBoxCrypto.Unwrap(Password, box.KdfPreset, box.Salt, box.Wrapped, box.Iv));
        opened.Should().Equal(CurrentDek());
    }

    [Fact]
    public async Task Login_WithoutBlindNode_BuildsNothing()
    {
        await AddPeerAsync(Guid.NewGuid(), "Laptop");

        await Triggers().OnSuperadminLogin(await AdminAsync(), Password);

        (await OwnStrongBoxesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Login_WhenAStrongBoxForTheCurrentKeyExists_DoesNotBuildAnother()
    {
        await AddPeerAsync(BlindNodeId.NewId(), "Blind");
        await Triggers().OnSuperadminLogin(await AdminAsync(), Password);

        await Triggers().OnSuperadminLogin(await AdminAsync(), Password);

        (await OwnStrongBoxesAsync(includeRetired: true)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Phone_NeverBuildsAStrongBox()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory = new RecoveryFactory(RecoveryHostKind.Phone, 16L << 30);
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
        await AddPeerAsync(BlindNodeId.NewId(), "Blind");

        await Triggers().OnSuperadminLogin(await AdminAsync(), Password);

        (await OwnStrongBoxesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LoginEndpoint_StartsTheBuild()
    {
        await AddPeerAsync(BlindNodeId.NewId(), "Blind");

        (await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();

        (await WaitUntilAsync(async () => (await OwnStrongBoxesAsync()).Count == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task PasswordChange_BuildsAStrongBoxUnderTheNewPassword()
    {
        await AddPeerAsync(BlindNodeId.NewId(), "Blind");
        const string newPassword = "ChangedPass2";

        (await _client.PostAsJsonAsync("/api/keys/change-password", new { oldPassword = Password, newPassword }))
            .EnsureSuccessStatusCode();

        (await WaitUntilAsync(async () => (await OwnStrongBoxesAsync()).Count == 1)).Should().BeTrue();
        var box = (await OwnStrongBoxesAsync()).Single();
        var opened = await HeavyDerivationQueue.RunAsync(() =>
            RecoveryBoxCrypto.TryUnwrap(newPassword, box.KdfPreset, box.Salt, box.Wrapped, box.Iv));
        opened.Should().Equal(CurrentDek());
    }

    [Fact]
    public async Task RotationAccepted_PublishesDeviceBox_ChainLink_AndStrongBox_ForTheNewKey()
    {
        await AddPeerAsync(BlindNodeId.NewId(), "Blind");
        var oldFp = CurrentFingerprint();

        var propose = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.EnsureSuccessStatusCode();
        var commitEventId = (await propose.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("commitEventId").GetGuid().ToString();
        (await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password }))
            .EnsureSuccessStatusCode();
        (await WaitUntilAsync(() => Task.FromResult(CurrentFingerprint() != oldFp))).Should().BeTrue();
        var newFp = CurrentFingerprint();
        var me = await MyNodeIdAsync();

        (await WaitUntilAsync(async () => (await OwnStrongBoxesAsync()).Any(b => b.DekFingerprint == newFp)))
            .Should().BeTrue("a strong box for the new key is built right after accept");
        using var conn = Db().CreateConnection();
        (await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_recovery_box WHERE kind = 'device' AND status = 'A'
              AND author_node_id = @Me COLLATE NOCASE AND dek_fingerprint = @Fp", new { Me = me, Fp = newFp }))
            .Should().Be(1, "the initiator's rewrapped slot is published as its device box");
        var link = await conn.QuerySingleAsync<(string Old, byte[] Wrapped, byte[] Iv)>(
            @"SELECT old_fingerprint, wrapped, iv FROM tbl_dek_retired_link
              WHERE commit_id = @C COLLATE NOCASE AND author_node_id = @Me COLLATE NOCASE AND new_fingerprint = @New",
            new { C = commitEventId, Me = me, New = newFp });
        link.Old.Should().Be(oldFp);
        DekFingerprint.Of(MasterKeyManager.UnwrapMasterDek(link.Wrapped, link.Iv, CurrentDek())).Should().Be(oldFp);
    }

    // --- cleanup ----------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_RetiresDeviceBoxesWithTheSamePasswordAndKey_KeepsTheOthers_AndReportsThem()
    {
        var phone1 = await AddPeerAsync(Guid.NewGuid(), "Phone1");
        var phone2 = await AddPeerAsync(Guid.NewGuid(), "Phone2");
        var covering = await PlantOwnStrongBoxAsync();
        var samePassword = await PlantDeviceBoxAsync(phone1, Password);
        var otherPassword = await PlantDeviceBoxAsync(phone2, "OtherPass9");

        var retired = await Cleanup().RunAsync(await AdminSlotAsync(), Password);

        retired.Should().BeEquivalentTo([samePassword]);
        (await StatusOfAsync(samePassword)).Should().Be(("R", covering));
        (await StatusOfAsync(otherPassword)).Should().Be(("A", (string?)null));

        _client.DefaultRequestHeaders.Add("X-User-Id", (await AdminAsync()).Id.ToString());
        var status = await _client.GetFromJsonAsync<RecoveryStatus>("/api/recovery/status");
        status!.DevicesWithOtherPassword.Should().Equal("Phone2");
        status.StrongBoxes.Should().Be(1);
        status.DeviceBoxes.Should().Be(1);
        status.CurrentKeyHasStrongBox.Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_RemembersWhatItTried()
    {
        var phone = await AddPeerAsync(Guid.NewGuid(), "Phone");
        await PlantOwnStrongBoxAsync();
        var box = await PlantDeviceBoxAsync(phone, "OtherPass9");
        await Cleanup().RunAsync(await AdminSlotAsync(), Password);

        using var conn = Db().CreateConnection();
        (await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT opens FROM tbl_recovery_box_check WHERE box_id = @B COLLATE NOCASE AND slot_id = @S",
            new { B = box, S = await AdminSlotAsync() })).Should().Be(0, "the answer is remembered as 'does not open'");
    }

    [Fact]
    public async Task Cleanup_WithoutOwnStrongBoxForTheCurrentKey_RetiresNothing()
    {
        var phone = await AddPeerAsync(Guid.NewGuid(), "Phone");
        var box = await PlantDeviceBoxAsync(phone, Password);

        var retired = await Cleanup().RunAsync(await AdminSlotAsync(), Password);

        retired.Should().BeEmpty();
        (await StatusOfAsync(box)).Status.Should().Be("A");
    }

    [Fact]
    public async Task Cleanup_OnlyOnAPc()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory = new RecoveryFactory(RecoveryHostKind.Hub, 8L << 30);
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
        var phone = await AddPeerAsync(Guid.NewGuid(), "Phone");
        await PlantOwnStrongBoxAsync();
        var box = await PlantDeviceBoxAsync(phone, Password);

        (await Cleanup().RunAsync(await AdminSlotAsync(), Password)).Should().BeEmpty();
        (await StatusOfAsync(box)).Status.Should().Be("A");
    }

    // --- helpers ----------------------------------------------------------------------------

    private RecoveryTriggers Triggers() => _factory.Services.GetRequiredService<RecoveryTriggers>();
    private RecoveryCleanupService Cleanup() => _factory.Services.GetRequiredService<RecoveryCleanupService>();
    private DbConnectionFactory Db() => _factory.Services.GetRequiredService<DbConnectionFactory>();
    private byte[] CurrentDek() => _factory.Services.GetRequiredService<SessionService>().GetMasterDek();
    private string CurrentFingerprint() => DekFingerprint.Of(CurrentDek());

    private async Task<User> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
    }

    private async Task<int> AdminSlotAsync() => (await AdminAsync()).KeySlotId!.Value;

    private async Task<Guid> AddPeerAsync(Guid nodeId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId, DisplayName = name, Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return nodeId;
    }

    private async Task<string> MyNodeIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId.ToString();
    }

    /// <summary>
    /// The cleanup never opens the covering box — it compares fingerprints — so its material can be
    /// anything well-formed.
    /// </summary>
    private async Task<string> PlantOwnStrongBoxAsync() =>
        await PlantBoxAsync(Guid.Parse(await MyNodeIdAsync()), "strong", "s512t6", CurrentFingerprint(),
            new byte[32], new byte[49], new byte[12]);

    private async Task<string> PlantDeviceBoxAsync(Guid author, string password)
    {
        var seal = RecoveryBoxCrypto.Wrap(CurrentDek(), password, RecoveryBoxKdf.Device64);
        return await PlantBoxAsync(author, "device", seal.KdfPreset, CurrentFingerprint(), seal.Salt, seal.Wrapped, seal.Iv);
    }

    private async Task<string> PlantBoxAsync(Guid author, string kind, string preset, string fp, byte[] salt, byte[] wrapped, byte[] iv)
    {
        var id = Guid.NewGuid().ToString();
        using var conn = Db().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv,
                created_at, status, lamport_ts, source_node_id)
              VALUES (@Id, @Kind, @Author, @Fp, 1, @Preset, @Salt, @Wrapped, @Iv, @Now, 'A', 1, @Author)",
            new { Id = id, Kind = kind, Author = author, Fp = fp, Preset = preset, Salt = salt, Wrapped = wrapped, Iv = iv, Now = DateTime.UtcNow.ToString("O") });
        return id;
    }

    private async Task<(string Status, string? RetiredBy)> StatusOfAsync(string boxId)
    {
        using var conn = Db().CreateConnection();
        return await conn.QuerySingleAsync<(string, string?)>(
            "SELECT status, retired_by_box_id FROM tbl_recovery_box WHERE box_id = @B COLLATE NOCASE", new { B = boxId });
    }

    private async Task<List<RecoveryBoxRow>> OwnStrongBoxesAsync(bool includeRetired = false)
    {
        var me = await MyNodeIdAsync();
        using var scope = _factory.Services.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<RecoveryBoxQueries>();
        if (!includeRetired)
            return (await queries.ActiveBoxesAsync())
                .Where(b => b.Kind == "strong" && string.Equals(b.AuthorNodeId, me, StringComparison.OrdinalIgnoreCase)).ToList();
        using var conn = Db().CreateConnection();
        return (await conn.QueryAsync<RecoveryBoxRow>(
            @"SELECT box_id AS BoxId, kind AS Kind, author_node_id AS AuthorNodeId, dek_fingerprint AS DekFingerprint,
                     epoch_hint AS EpochHint, kdf_preset AS KdfPreset, salt AS Salt, wrapped AS Wrapped, iv AS Iv,
                     created_at AS CreatedAt, status AS Status, retired_by_box_id AS RetiredByBoxId,
                     lamport_ts AS LamportTs, source_node_id AS SourceNodeId
              FROM tbl_recovery_box WHERE kind = 'strong'")).ToList();
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private sealed class SuperadminStanding : IOwnStandingProvider
    {
        public Task<bool> IsSuperadminAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeHost(RecoveryHostKind kind, long available) : IRecoveryHost
    {
        public RecoveryHostKind Kind => kind;
        public long AvailableMemoryBytes() => available;
    }

    private sealed class RecoveryFactory(RecoveryHostKind kind, long available) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IRecoveryHost>(new FakeHost(kind, available));
                s.AddSingleton<IOwnStandingProvider>(new SuperadminStanding());
            });
        }
    }
}
