using System.Text;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A full package made by a SEALED (encrypted) full node. Its <c>control.db</c> is a plain SQLite file by design (spec 8.4: the blind side
/// opens it without any key), but the writer copies the control tables through an ATTACH on the node's own keyed connection, and an ATTACH with
/// no <c>KEY</c> clause gives the new file the main database's key: the blind node then refused every first seed from an encrypted node with
/// "SQLite Error 26: file is not a database".
/// </summary>
public sealed class Format2SealedPackageTests : IAsyncLifetime
{
    private Format2TestNode _node = null!;
    private Format2TestKit.Producer _producer = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_f2sealed_" + Guid.NewGuid().ToString("N"));
    private BaselineFiles _baseline = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_work);
        _node = await Format2TestNode.CreateAsync(name: "sealed-hub", sealedVault: true);
        _producer = Format2TestKit.Producer.Create();
        await _node.TrustAsync(_producer.NodeId, _producer.PublicKey, superadmin: true, name: "producer");
        await Format2TestKit.ActivateAsync(_node.Factory, _producer.NodeId);
        var blobs = new List<string>();
        for (var i = 0; i < 3; i++) blobs.Add(await Format2TestKit.StoreBlobAsync(_node.Factory));
        _baseline = Format2TestKit.WriteBaseline(Path.Combine(_work, "baseline"), _producer, blobs);
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_full_package_of_a_sealed_node_carries_a_plain_control_db_the_blind_side_can_open()
    {
        _node.Factory.IsSealed.Should().BeTrue("the node under test is encrypted, like the work Bibank");

        var package = await _node.Writer().WriteAsync(new BlindPackageV2Request(
            BlindPackageKinds.Full, Guid.NewGuid(), new ServableBaseline(_baseline, 0), null, true));
        var verified = await new BlindPackageV2Reader().ExtractVerifiedAsync(package.FilePath, package.DetachedSignature,
            _node.Identity.Ed25519PublicKey, _producer.TrustedAs(superadmin: true), Path.Combine(_work, "read"));

        var head = new byte[16];
        using (var f = File.OpenRead(verified.ControlDbPath!)) f.ReadExactly(head);
        Encoding.ASCII.GetString(head, 0, 15).Should().Be("SQLite format 3", "control.db is plain: a blind node holds no key to open anything else");

        using var control = new SqliteConnection($"Data Source={verified.ControlDbPath};Mode=ReadOnly;Pooling=False");
        control.Open();
        control.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
            .Should().BeEquivalentTo(SnapshotTables.Control, "control.db holds exactly the control tables (spec 8.4)");
        control.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_metadata_activation").Should().Be(1);
    }
}
