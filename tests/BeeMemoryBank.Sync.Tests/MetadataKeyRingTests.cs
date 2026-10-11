using System.Security.Cryptography;
using BeeMemoryBank.Vault.Metadata;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 4.5, 4.8): the key ring opens a MEK lazily with the session's candidate DEKs, picks
/// the highest generation it can open, and forgets every open key when the vault locks.
/// </summary>
public class MetadataKeyRingTests : IAsyncLifetime
{
    private readonly ConcreteFixture _node = new();
    private MetadataKeyRing _ring = null!;

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        _ring = new MetadataKeyRing(_node.Factory, _node.Session);
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    [Fact]
    public async Task ALockedNode_OpensNothing()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var (mek, row) = MetadataKeyCrypto.Create(1, dek);
        await InsertAsync(row);

        _ring.Current.Should().BeNull();
        _ring.CanOpen(row.KeyId).Should().BeFalse();
        FluentActions.Invoking(() => _ring.DeriveKey(row.KeyId, MetadataKeyCrypto.EventKeyInfo, [])).Should().Throw<MetadataKeyUnavailableException>();

        _node.Session.UnlockWithDek(dek);
        _ring.Current.Should().Be(new MetadataKeyInfo(row.KeyId, 1));
        _ring.CanOpen(row.KeyId).Should().BeTrue();
        _ring.DeriveKey(row.KeyId, MetadataKeyCrypto.EventKeyInfo, []).Should().Equal(MetadataKeyCrypto.DeriveEventKey(mek));

        _node.Session.Lock();
        _ring.Current.Should().BeNull("the open keys go with the lock");
        _ring.CanOpen(row.KeyId).Should().BeFalse();
        FluentActions.Invoking(() => _ring.DeriveKey(row.KeyId, MetadataKeyCrypto.EventKeyInfo, [])).Should().Throw<MetadataKeyUnavailableException>();
    }

    [Fact]
    public async Task TheCurrentKey_IsTheHighestGenerationThisNodeCanOpen_ThenTheGreatestKeyId()
    {
        var dek1 = RandomNumberGenerator.GetBytes(32);
        var dek2 = RandomNumberGenerator.GetBytes(32);
        var (_, gen1) = MetadataKeyCrypto.Create(1, dek1);
        var (_, gen2) = MetadataKeyCrypto.Create(2, dek2);
        await InsertAsync(gen1);
        await InsertAsync(gen2);

        _node.Session.UnlockWithDek(dek1);
        _ring.Current!.Generation.Should().Be(1, "generation 2 is wrapped under a DEK this node does not hold (left out of a rotation)");
        _ring.CanOpen(gen2.KeyId).Should().BeFalse();

        // The rotation reaches this node: the old DEK stays a candidate, so both open and the newer seals.
        _node.Session.SwapMasterDek((byte[])dek2.Clone());
        _ring.Current!.KeyId.Should().Be(gen2.KeyId);
        _ring.CanOpen(gen1.KeyId).Should().BeTrue("old generations keep opening old events");

        // Two keys of one generation (two rotations racing): the ordinally greatest id seals, on every node alike.
        var (_, a) = MetadataKeyCrypto.Create(2, dek2);
        var (_, b) = MetadataKeyCrypto.Create(2, dek2);
        await InsertAsync(a);
        await InsertAsync(b);
        var greatest = new[] { gen2.KeyId, a.KeyId, b.KeyId }.Max(StringComparer.Ordinal)!;
        _ring.Current!.KeyId.Should().Be(greatest);
    }

    [Fact]
    public async Task AForgedRow_ThatDoesNotOpen_IsNeverCurrent()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var (_, good) = MetadataKeyCrypto.Create(1, dek);
        var (_, forged) = MetadataKeyCrypto.Create(5, dek);
        forged = forged with { Wrapped = RandomNumberGenerator.GetBytes(48) };
        await InsertAsync(good);
        await InsertAsync(forged);

        _node.Session.UnlockWithDek(dek);
        _ring.Current!.KeyId.Should().Be(good.KeyId);
        _ring.CanOpen(forged.KeyId).Should().BeFalse();
    }

    private async Task InsertAsync(MetadataKeyRow row)
    {
        using var conn = _node.Factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
              VALUES (@KeyId, @Generation, @DekFingerprint, 0, @Wrapped, @Iv, @Author, @Now, 0, @EventId)",
            new { row.KeyId, row.Generation, row.DekFingerprint, row.Wrapped, row.Iv, Author = Guid.NewGuid().ToString(),
                Now = DateTime.UtcNow.ToString("O"), EventId = Guid.NewGuid().ToString() });
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
