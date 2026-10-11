using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A full node after the metadata activation (docs/design/step-a-spec.md 3.9, 5.7): a protocol-3 content event from a 2.6
/// node that wrote it before it pulled the switch (a "straggler") is applied as today and makes the next baseline early;
/// a sealed one does not. The node itself writes content only sealed, and refuses to write any while it holds no metadata
/// key it can open: a write that cannot be sealed is not written at all, never in clear.
/// </summary>
public class ActivatedNodeTests : IAsyncLifetime
{
    private readonly ConcreteFixture _node = new();
    private readonly StepAAuthor _activator = new();
    private readonly StepAAuthor _late = new();
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(32);

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        await _node.NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(), DisplayName = "Receiver", Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
            Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow
        });
        await _node.WhitelistRepo.CreateAsync(_activator.Entry("Activator", superadmin: true));
        await _node.WhitelistRepo.CreateAsync(_late.Entry("Late", superadmin: false));
        _node.Session.UnlockWithDek((byte[])_dek.Clone());
    }

    public async Task DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_dek);
        await _node.DisposeAsync();
    }

    [Fact]
    public async Task ProtocolThreeContent_BeforeTheSwitch_IsAppliedWithoutAnEarlyBaseline()
    {
        (await _node.EventApplier.ApplyAsync(FolderFromLateNode("/Before"))).Should().Be(EventApplyResult.Applied);

        _node.BaselineScheduler.Reasons.Should().BeEmpty();
    }

    [Fact]
    public async Task AStraggler_AfterTheSwitch_IsApplied_AndMakesTheNextBaselineEarly_ASealedEventDoesNot()
    {
        await SwitchAsync();

        (await _node.EventApplier.ApplyAsync(FolderFromLateNode("/Straggler"))).Should().Be(EventApplyResult.Applied);
        _node.BaselineScheduler.Reasons.Should().Equal(BaselineReasons.LateProtocol3);
        (await new FolderRepository(_node.Factory, new BeeMemoryBank.Core.Services.CallerScopeHolder()).GetByPathAsync("/Straggler"))
            .Should().NotBeNull("a straggler is legitimate and applied as today");

        var sealedFolder = _activator.Sealed(EventTypes.FolderCreate, null,
            StepAAuthor.FolderCreate(Guid.NewGuid(), "/Sealed", "Sealed"));
        (await _node.EventApplier.ApplyAsync(sealedFolder)).Should().Be(EventApplyResult.Applied);
        _node.BaselineScheduler.Reasons.Should().Equal([BaselineReasons.LateProtocol3], "a sealed event needs no early baseline");
    }

    [Fact]
    public async Task AfterTheSwitch_TheNodeWritesSealed_WithItsLocalColumns()
    {
        await SwitchAsync();

        var article = await _node.ArticleService.CreateAsync("Local title", "/Local", [], "body");

        using var conn = _node.Factory.CreateConnection();
        var rows = (await conn.QueryAsync<(string Type, long Protocol, string? ArticleId, string? LocalType, string? LocalArticleId)>(
            "SELECT event_type, protocol_version, article_id, local_type, local_article_id FROM tbl_event WHERE node_id = @Id COLLATE NOCASE",
            new { Id = (await _node.NodeRepo.GetAsync())!.NodeId.ToString() })).ToList();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Type == EventTypes.Sealed && r.Protocol == EventClasses.SealedProtocol && r.ArticleId == null);
        rows.Should().Contain(r => r.LocalType == EventTypes.ArticleCreate
                                   && string.Equals(r.LocalArticleId, article.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AfterTheSwitch_WithoutAKeyItCanOpen_TheNodeRefusesToWrite_NothingInClear()
    {
        // The network switched under a key wrapped by a DEK this node does not hold.
        var otherDek = RandomNumberGenerator.GetBytes(32);
        (await _node.EventApplier.ApplyAsync(_activator.NewKey(otherDek))).Should().Be(EventApplyResult.Applied);
        (await _node.EventApplier.ApplyAsync(_activator.Activation())).Should().Be(EventApplyResult.Applied);
        _node.ProtocolPolicy.SealsMetadata.Should().BeTrue();
        _node.KeyRing.Current.Should().BeNull();
        var before = await _node.EventLogRepo.GetMaxSequenceAsync();

        var write = () => _node.ArticleService.CreateAsync("Cannot be sealed", "/Nowhere", [], "body");

        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no metadata key*");
        (await _node.EventLogRepo.GetMaxSequenceAsync()).Should().Be(before, "nothing was logged");
        using var conn = _node.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article")).Should().Be(0, "the write was rolled back");
    }

    /// <summary>The activator's generation 1 under this node's DEK, then the switch: what a sync from it delivers.</summary>
    private async Task SwitchAsync()
    {
        (await _node.EventApplier.ApplyAsync(_activator.NewKey(_dek))).Should().Be(EventApplyResult.Applied);
        (await _node.EventApplier.ApplyAsync(_activator.Activation())).Should().Be(EventApplyResult.Applied);
        _node.ProtocolPolicy.SealsMetadata.Should().BeTrue();
        _node.KeyRing.Current.Should().NotBeNull();
    }

    private SyncEvent FolderFromLateNode(string path) =>
        _late.Clear(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), path, path.TrimStart('/')));

    private sealed class ConcreteFixture : SyncTestFixture { }
}
