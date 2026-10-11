using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Package format 2 (docs/design/step-a-spec.md 8, red tests 16.3 item 7): what the writer puts in, what the reader refuses.
/// The serving node holds plaintext metadata in its content tables and clear protocol-3 content in its log (canaries in
/// every field); none of it may reach any entry of the package. The baseline is hand-made: random container bytes signed by
/// a test producer key, as the blind side only ever copies and checks it.
/// </summary>
public sealed class Format2PackageTests : IAsyncLifetime
{
    private Format2TestNode _node = null!;
    private Format2TestKit.Producer _producer = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_f2pkg_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _canaries = [];
    private readonly List<string> _blobs = [];
    private SyncEvent _clearContent = null!;
    private SyncEvent _sealed = null!;
    private BaselineFiles _baseline = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_work);
        _node = await Format2TestNode.CreateAsync(name: "hub");
        _producer = Format2TestKit.Producer.Create();
        await _node.TrustAsync(_producer.NodeId, _producer.PublicKey, superadmin: true, name: "producer");
        await Format2TestKit.ActivateAsync(_node.Factory, _producer.NodeId);

        // Plaintext metadata in the serving node's own tables (a full hub holds it until Step B) and in its log.
        var title = MetadataCanaries.Ascii("title");
        var folder = MetadataCanaries.NonAscii("folder");
        _canaries.AddRange([title, folder]);
        using (var conn = _node.Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, @title, @folder, 'A', @now, @now)",
                new { id = Guid.NewGuid().ToString(), title, folder, now = DateTime.UtcNow.ToString("O") });
        }

        for (var i = 0; i < 3; i++) _blobs.Add(await Format2TestKit.StoreBlobAsync(_node.Factory));
        var deltaBlob = await Format2TestKit.StoreBlobAsync(_node.Factory);
        _blobs.Add(deltaBlob);

        _clearContent = Format2TestKit.ClearArticleEvent(_producer.NodeId, _producer.PrivateKey, 10, title, folder);
        _sealed = Format2TestKit.SealedEvent(_producer.NodeId, _producer.PrivateKey, 11, [deltaBlob]);
        await _node.Events.AppendAsync(_clearContent);
        await _node.Events.AppendAsync(_sealed);
        await _node.Events.AppendAsync(Format2TestKit.SealedEvent(_producer.NodeId, _producer.PrivateKey, 12));

        _baseline = Format2TestKit.WriteBaseline(Path.Combine(_work, "baseline"), _producer, _blobs.Take(3));
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private Task<BlindPackageV2> WriteAsync(string kind = BlindPackageKinds.Full, bool blobs = true, BaselineFiles? baseline = null, long cut = 0) =>
        _node.Writer().WriteAsync(new BlindPackageV2Request(kind, Guid.NewGuid(), new ServableBaseline(baseline ?? _baseline, cut), null,
            blobs && kind == BlindPackageKinds.Full));

    private Task<VerifiedPackageV2> ReadAsync(string archive, byte[] detached, Func<Guid, (byte[], bool)?>? trusted = null, byte[]? servingKey = null) =>
        new BlindPackageV2Reader().ExtractVerifiedAsync(archive, detached, servingKey ?? _node.Identity.Ed25519PublicKey,
            trusted ?? _producer.TrustedAs(superadmin: true), Path.Combine(_work, "read-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task FullPackage_RoundTrips_WithControlTablesDeltasAndBlobs()
    {
        var package = await WriteAsync();
        var verified = await ReadAsync(package.FilePath, package.DetachedSignature);

        verified.Manifest.Kind.Should().Be(BlindPackageKinds.Full);
        verified.Manifest.Format.Should().Be("bmb-blind-package-v2");
        verified.Manifest.BaselineId.Should().Be(_baseline.Manifest.BaselineId);
        verified.Manifest.ProducerNodeId.Should().Be(_node.Identity.NodeId, "the manifest names the SERVING node");
        verified.Manifest.Activation.Should().NotBeNull();
        verified.Manifest.ContainsBlobs.Should().BeTrue();
        verified.Baseline.ManifestBytes.Should().Equal(_baseline.ManifestBytes);
        File.ReadAllBytes(Path.Combine(verified.Baseline.Directory, BaselineManifest.ContainerFileName))
            .Should().Equal(File.ReadAllBytes(Path.Combine(_baseline.Directory, BaselineManifest.ContainerFileName)));

        var deltas = new List<SyncEvent>();
        await foreach (var e in Format2Package.ReadDeltasAsync(verified.DeltasPath!)) deltas.Add(e);
        deltas.Should().HaveCount(2, "the protocol-3 content row stays out (spec 5.7)");
        deltas.Select(d => d.EventId).Should().NotContain(_clearContent.EventId);
        deltas[0].Payload.Should().Be(_sealed.Payload, "a delta is the stored signed event, byte for byte");
        deltas[0].Signature.Should().Equal(_sealed.Signature);
        deltas[0].ActorName.Should().BeNull();
        verified.Manifest.DeltaCount.Should().Be(2);

        foreach (var hash in _blobs)
            File.Exists(Path.Combine(verified.BlobsDirectory!, hash)).Should().BeTrue($"blob {hash} is in the baseline or a delta");

        using var control = new SqliteConnection($"Data Source={verified.ControlDbPath};Mode=ReadOnly;Pooling=False");
        control.Open();
        control.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
            .Should().BeEquivalentTo(SnapshotTables.Control, "control.db holds exactly the control tables (spec 8.4)");
        control.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_metadata_key").Should().Be(1);
        control.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_metadata_activation").Should().Be(1);
    }

    [Fact]
    public async Task NoCanaryInAnyEntry_NorInTheOuterManifest_TheScannerSeesThemInTheClearRow()
    {
        // Control: the scanner finds the canaries where they are (the clear row's payload, the serving database).
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(_clearContent.Payload), _canaries, "clear row").Should().NotBeEmpty();
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(Format2Package.ToDeltaLine(_clearContent)), _canaries, "a delta line of it")
            .Should().NotBeEmpty("had the writer kept it, its delta line would carry the title");

        var package = await WriteAsync();
        MetadataCanaries.ScanArchive(package.FilePath, _canaries).Should().BeEmpty();
        foreach (var (name, data) in Format2TestKit.ReadEntries(package.FilePath))
        {
            MetadataCanaries.Scan(data, _canaries, name).Should().BeEmpty();
            if (name.EndsWith(".db", StringComparison.Ordinal) || name == Format2Package.BaselineContainerEntry) continue;
            name.Should().NotBe("beememorybank.db", "a format-2 package has no database entry");
        }
    }

    [Fact]
    public async Task TheContainer_NeverStartsWithASQLiteHeader_AndTheWriterRefusesOneThatDoes()
    {
        var package = await WriteAsync();
        var container = Format2TestKit.ReadEntries(package.FilePath).Single(e => e.Name == Format2Package.BaselineContainerEntry).Data;
        Encoding.ASCII.GetString(container, 0, 16).Should().NotStartWith("SQLite format 3");

        var plain = Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[512]).ToArray();
        var bad = Format2TestKit.WriteBaseline(Path.Combine(_work, "plain-baseline"), _producer, container: plain);
        var write = () => WriteAsync(baseline: bad);
        await write.Should().ThrowAsync<InvalidDataException>("a plaintext database is never served as a baseline");
    }

    [Fact]
    public async Task AFormat1Reader_RefusesAFormat2Package()
    {
        var package = await WriteAsync();
        var blindManifest = Format2TestKit.ReadEntries(package.FilePath).Single(e => e.Name == BlindManifest.FileName).Data;
        var parse = () => BlindManifest.Parse(blindManifest);
        parse.Should().Throw<InvalidDataException>().WithMessage("*bmb-blind-package-v2*");
        Format2TestKit.ReadEntries(package.FilePath).Select(e => e.Name).Should().NotContain("beememorybank.db",
            "a 2.5 reader that got past the format string still finds no database");
    }

    [Fact]
    public async Task BaselinePackage_CarriesOnlyTheBaseline()
    {
        var package = await WriteAsync(BlindPackageKinds.Baseline);
        var verified = await ReadAsync(package.FilePath, package.DetachedSignature);
        verified.Manifest.Kind.Should().Be(BlindPackageKinds.Baseline);
        verified.ControlDbPath.Should().BeNull();
        verified.DeltasPath.Should().BeNull();
        verified.BlobsDirectory.Should().BeNull();
        Format2TestKit.ReadEntries(package.FilePath).Select(e => e.Name).Should().BeEquivalentTo(
            ["blind-manifest.json", "baseline/manifest.json", "baseline/manifest.json.sig", "baseline/blobs.txt",
             "baseline/baseline.bmbch", "manifest.json", "manifest.json.sig"]);
    }

    /// <summary>A baseline of a vault without bodies or media lists no blob: its blobs.txt is empty, and a zero-length tar entry is still a file.</summary>
    [Fact]
    public async Task ABaselineWithoutBlobs_RoundTrips()
    {
        var empty = Format2TestKit.WriteBaseline(Path.Combine(_work, "no-blobs"), _producer, []);
        var package = await WriteAsync(BlindPackageKinds.Baseline, baseline: empty);
        var verified = await ReadAsync(package.FilePath, package.DetachedSignature);
        verified.Baseline.Manifest.BlobCount.Should().Be(0);
        File.ReadAllBytes(Path.Combine(verified.Baseline.Directory, BaselineManifest.BlobListFileName)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeltasStartAfterTheCut()
    {
        var head = await _node.Events.GetMaxSequenceAsync();
        var package = await WriteAsync(cut: head - 1);
        var verified = await ReadAsync(package.FilePath, package.DetachedSignature);
        verified.Manifest.CutInServerLog.Should().Be(head - 1);
        verified.Manifest.DeltaCount.Should().Be(1, "only the last row is after the cut");
    }

    public static IEnumerable<object[]> Tampers() =>
    [
        ["container byte", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, Format2Package.BaselineContainerEntry, d => { d[0] ^= 1; return d; }))],
        ["blob list", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, Format2Package.BaselineBlobListEntry, d => [.. d, .. "0\n"u8.ToArray()]))],
        ["delta line", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, BlindManifestV2.DeltasFileName, d => d[..^5]))],
        ["control.db", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, BlindManifestV2.ControlDbFileName, d => { d[^1] ^= 1; return d; }))],
        ["blind manifest", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, BlindManifestV2.FileName, d => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace("\"full\"", "\"baseline\""))))],
        ["outer manifest", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, Format2Package.ManifestFileName, d => [.. d, (byte)' ']))],
        ["embedded signature", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => Change(e, Format2Package.ManifestSignatureFileName, d => { d[3] ^= 1; return d; }))],
        ["a database entry added", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => [.. e, ("beememorybank.db", new byte[16])])],
        ["an entry removed", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => e.Where(x => !x.Name.StartsWith("blobs/", StringComparison.Ordinal)).ToList())],
        ["an entry twice", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => [.. e, e.First(x => x.Name == BlindManifestV2.DeltasFileName)])],
        ["a path that escapes", (Func<List<(string Name, byte[] Data)>, List<(string, byte[])>>)(e => [.. e, ("../evil", new byte[1])])],
    ];

    /// <summary>Any change to any entry, an entry added or removed, is refused (spec 8.6 step 1), with or without the detached signature.</summary>
    [Theory]
    [MemberData(nameof(Tampers))]
    public async Task ATamperedPackage_IsRefused(string what, Func<List<(string Name, byte[] Data)>, List<(string, byte[])>> tamper)
    {
        var package = await WriteAsync();
        var tampered = Path.Combine(_work, "tampered-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        Format2TestKit.WriteEntries(tampered, tamper(Format2TestKit.ReadEntries(package.FilePath)));

        var withEmbeddedOnly = () => ReadAsync(tampered, []);
        await withEmbeddedOnly.Should().ThrowAsync<InvalidDataException>(what);
        var withDetached = () => ReadAsync(tampered, package.DetachedSignature);
        await withDetached.Should().ThrowAsync<InvalidDataException>(what);
    }

    [Fact]
    public async Task TheDetachedSignature_IsChecked_OverTheWholeArchive()
    {
        var package = await WriteAsync();
        var wrong = (byte[])package.DetachedSignature.Clone();
        wrong[0] ^= 1;
        var read = () => ReadAsync(package.FilePath, wrong);
        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*detached signature*");
    }

    [Fact]
    public async Task APackageSignedByAnotherNode_IsRefused()
    {
        var package = await WriteAsync();
        var (otherKey, _) = Ed25519Signer.GenerateKeyPair();
        var read = () => ReadAsync(package.FilePath, package.DetachedSignature, servingKey: otherKey);
        await read.Should().ThrowAsync<InvalidDataException>();
        var embeddedOnly = () => ReadAsync(package.FilePath, [], servingKey: otherKey);
        await embeddedOnly.Should().ThrowAsync<InvalidDataException>().WithMessage("*signature*");
    }

    /// <summary>Spec 8.6 step 3 and 16.3 item 3: the baseline's producer and its own signature, size and hashes.</summary>
    [Fact]
    public async Task TheBaseline_IsCheckedAgainstTheReceiversTrust()
    {
        var package = await WriteAsync();

        var unknown = () => ReadAsync(package.FilePath, package.DetachedSignature, trusted: _ => null);
        await unknown.Should().ThrowAsync<InvalidDataException>().WithMessage("*producer is not a node this receiver trusts*");

        var plain = () => ReadAsync(package.FilePath, package.DetachedSignature, trusted: _producer.TrustedAs(superadmin: false));
        await plain.Should().ThrowAsync<InvalidDataException>().WithMessage("*not a superadmin*");

        var (impostor, _) = Ed25519Signer.GenerateKeyPair();
        var otherKey = () => ReadAsync(package.FilePath, package.DetachedSignature, trusted: id => (impostor, true));
        await otherKey.Should().ThrowAsync<InvalidDataException>().WithMessage("*baseline's signature*");
    }

    [Theory]
    [InlineData("container")]
    [InlineData("blob list")]
    [InlineData("signature")]
    public async Task ABaselineChangedAfterItWasSigned_IsRefused_EvenFromATrustedServingNode(string what)
    {
        var bad = Format2TestKit.WriteBaseline(Path.Combine(_work, "bad-" + what.Replace(' ', '-')), _producer, _blobs.Take(1), tamper: dir =>
        {
            switch (what)
            {
                case "container":
                    var c = Path.Combine(dir, BaselineManifest.ContainerFileName);
                    var bytes = File.ReadAllBytes(c); bytes[10] ^= 1; File.WriteAllBytes(c, bytes);
                    break;
                case "blob list":
                    File.WriteAllBytes(Path.Combine(dir, BaselineManifest.BlobListFileName), Format2Package.FormatBlobList(_blobs.Take(2)));
                    break;
                case "signature":
                    var s = Path.Combine(dir, BaselineManifest.SignatureFileName);
                    var sig = File.ReadAllBytes(s); sig[5] ^= 1; File.WriteAllBytes(s, sig);
                    break;
            }
        });
        // A writer that checks what it copies refuses a changed container or blob list (TheWriter_Refuses...); the receiver still does
        // not rely on that, so the changed baseline is put into a package the serving node signed all the same.
        var baseline = what == "signature" ? bad with { Signature = File.ReadAllBytes(Path.Combine(bad.Directory, BaselineManifest.SignatureFileName)) } : bad;
        var good = await WriteAsync(BlindPackageKinds.Baseline);
        var (path, detached) = await ResignAsync(good.FilePath, entries => SwapBaseline(entries, baseline));
        var read = () => ReadAsync(path, detached);
        await read.Should().ThrowAsync<InvalidDataException>().WithMessage(what switch
        {
            "container" => "*container does not match its signed hash*",
            "blob list" => "*blob list does not match its signed hash*",
            _ => "*baseline's signature does not verify*",
        });
    }

    [Fact]
    public async Task AFullPackage_RefusesToBeWritten_WhenABlobItNamesIsNotStored()
    {
        var missing = new string('c', 64);
        var baseline = Format2TestKit.WriteBaseline(Path.Combine(_work, "missing-blob"), _producer, [missing]);
        var write = () => WriteAsync(baseline: baseline);
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    public static IEnumerable<object[]> NotAContainer() => Format2TestKit.NotAContainerCases.Select(c => new object[] { c });

    /// <summary>
    /// Spec 7.1 (review round): a baseline container has the BMBCH1 framing. Another plaintext format that merely does not start
    /// like SQLite, or a frame broken in any one way, is refused by the writer and by a reader that is handed a signed package
    /// carrying it (the receiver does not rely on the sender having checked).
    /// </summary>
    [Theory]
    [MemberData(nameof(NotAContainer))]
    public async Task ABaselineContainerThatIsNotBmbch1_IsRefused_ByTheWriter_AndByTheReader(string what)
    {
        var bad = Format2TestKit.WriteBaseline(Path.Combine(_work, "nc-" + Guid.NewGuid().ToString("N")), _producer, _blobs.Take(3),
            container: Format2TestKit.NotAContainer(what));
        var write = () => WriteAsync(BlindPackageKinds.Baseline, baseline: bad);
        await write.Should().ThrowAsync<InvalidDataException>(what);

        var good = await WriteAsync(BlindPackageKinds.Baseline);
        var (path, detached) = await ResignAsync(good.FilePath, entries => SwapBaseline(entries, bad));
        var withDetached = () => ReadAsync(path, detached);
        await withDetached.Should().ThrowAsync<InvalidDataException>(what);
        var embeddedOnly = () => ReadAsync(path, []);
        await embeddedOnly.Should().ThrowAsync<InvalidDataException>(what);
    }

    [Fact]
    public async Task AContainerOfSeveralChunks_PassesTheFraming()
    {
        var many = Format2TestKit.WriteBaseline(Path.Combine(_work, "many"), _producer, _blobs.Take(3),
            container: Format2TestKit.OpaqueContainer(Format2TestKit.KeyId, chunk: 4096, plaintextBytes: 10 * 4096));
        var package = await WriteAsync(BlindPackageKinds.Baseline, baseline: many);
        (await ReadAsync(package.FilePath, package.DetachedSignature)).Baseline.Manifest.ContainerBytes.Should().Be(many.Manifest.ContainerBytes);
    }

    /// <summary>The writer compares what it copies with what the baseline manifest signed, so a changed file is never packed.</summary>
    [Theory]
    [InlineData("container")]
    [InlineData("blob list")]
    public async Task TheWriter_RefusesABaselineWhoseFilesChangedAfterSigning(string what)
    {
        var bad = Format2TestKit.WriteBaseline(Path.Combine(_work, "chg-" + what.Replace(' ', '-')), _producer, _blobs.Take(1), tamper: dir =>
        {
            if (what == "container")
            {
                var c = Path.Combine(dir, BaselineManifest.ContainerFileName);
                var bytes = File.ReadAllBytes(c);
                bytes[^3] ^= 1;
                File.WriteAllBytes(c, bytes);
            }
            else
            {
                File.WriteAllBytes(Path.Combine(dir, BaselineManifest.BlobListFileName), Format2Package.FormatBlobList(_blobs.Take(2)));
            }
        });
        var write = () => WriteAsync(BlindPackageKinds.Baseline, baseline: bad);
        await write.Should().ThrowAsync<InvalidDataException>().WithMessage("*signed*");
    }

    private SyncEvent Clone(SyncEvent e, Action<SyncEvent>? set = null)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<SyncEvent>(Format2Package.ToDeltaLine(e), Format2Package.DeltaJson)!;
        set?.Invoke(copy);
        return copy;
    }

    /// <summary>
    /// A package the serving node signed with <paramref name="change"/> applied to its delta lines (and the delta count of the
    /// blind manifest set to the number of non-empty lines): what a buggy or hostile serving node could send.
    /// </summary>
    private async Task<(string Path, byte[] Detached)> WithDeltasAsync(Func<List<SyncEvent>, List<string>> change, long cut = 0)
    {
        var package = await WriteAsync(cut: cut);
        return await ResignAsync(package.FilePath, entries =>
        {
            var deltas = entries.Single(e => e.Name == BlindManifestV2.DeltasFileName).Data;
            var events = Encoding.UTF8.GetString(deltas).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<SyncEvent>(l, Format2Package.DeltaJson)!).ToList();
            var lines = change(events);
            var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
            return entries.Select(e =>
            {
                if (e.Name == BlindManifestV2.DeltasFileName) return (e.Name, bytes);
                if (e.Name == BlindManifestV2.FileName)
                    return (e.Name, (BlindManifestV2.Parse(e.Data) with { DeltaCount = lines.Count(l => l.Length > 0) }).ToBytes());
                return (e.Name, e.Data);
            }).ToList();
        });
    }

    public static IEnumerable<object[]> BadDeltaStreams() =>
    [
        ["a protocol-3 content row", "*content event*"],
        ["a row with a negative Lamport time", "*Lamport*"],
        ["a row past the Lamport limit", "*Lamport*"],
        ["rows out of order", "*does not come after*"],
        ["the same event twice", "*twice*"],
        ["a row at the cut", "*cut*"],
        ["a row without a sequence number", "*cut*"],
        ["a row after the head", "*head*"],
        ["a blank line", "*empty*"],
        ["a line that is no event", "*row 2*"],
    ];

    /// <summary>
    /// Spec 8.5 and 8.6 step 5 (review round): the reader refuses a delta stream that breaks the invariants of the format
    /// instead of trusting the sender - every row after the cut and not after the head, strictly in order, each event once,
    /// no clear content - before anything reaches the applier or the disk of a receiver.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadDeltaStreams))]
    public async Task ADeltaStreamThatBreaksTheFormat_IsRefused_AlthoughTheServingNodeSignedIt(string what, string message)
    {
        var log = await _node.Events.GetAllAfterSequenceAsync(0, 10);
        var clear = log.Single(e => e.EventId == _clearContent.EventId);
        long cut = what == "a row at the cut" ? 1 : 0;
        var head = log.Max(e => e.SequenceNum);
        var (path, detached) = await WithDeltasAsync(deltas =>
        {
            var d1 = deltas[0];
            var d2 = deltas[1];
            return what switch
            {
                "a protocol-3 content row" => [Line(Clone(clear)), Line(d1), Line(d2)],
                "a row with a negative Lamport time" => [Line(Clone(d1, c => { c.EventId = Guid.NewGuid(); c.LamportTs = -1; })), Line(d2)],
                "a row past the Lamport limit" => [Line(Clone(d1, c => { c.EventId = Guid.NewGuid(); c.LamportTs = BlindManifestV2.MaximumLamport + 1; })), Line(d2)],
                "rows out of order" => [Line(d2), Line(d1)],
                "the same event twice" => [Line(d1), Line(Clone(d1, c => c.SequenceNum = d2.SequenceNum))],
                "a row at the cut" => [Line(Clone(d1, c => { c.EventId = Guid.NewGuid(); c.SequenceNum = 1; })), Line(d1), Line(d2)],
                "a row without a sequence number" => [Line(Clone(d1, c => { c.EventId = Guid.NewGuid(); c.SequenceNum = 0; })), Line(d1), Line(d2)],
                "a row after the head" => [Line(d1), Line(d2), Line(Clone(d2, c => { c.EventId = Guid.NewGuid(); c.SequenceNum = head + 1; }))],
                "a blank line" => [Line(d1), "", Line(d2)],
                "a line that is no event" => [Line(d1), "null", Line(d2)],
                _ => throw new ArgumentException(what),
            };
        }, cut);

        var withDetached = () => ReadAsync(path, detached);
        await withDetached.Should().ThrowAsync<InvalidDataException>().WithMessage(message, what);
        var embeddedOnly = () => ReadAsync(path, []);
        await embeddedOnly.Should().ThrowAsync<InvalidDataException>(what);
    }

    [Fact]
    public async Task TheDeltaStream_OfAWrittenPackage_PassesTheChecks_AndTheCutIsExclusive()
    {
        var head = await _node.Events.GetMaxSequenceAsync();
        var package = await WriteAsync(cut: head - 1);
        var verified = await ReadAsync(package.FilePath, package.DetachedSignature);
        verified.Manifest.DeltaCount.Should().Be(1);
        verified.Manifest.CpSequence.Should().Be(head);
    }

    /// <summary>
    /// A package the serving node signed with <paramref name="edit"/> applied to the JSON of its blind-manifest.json: what a
    /// buggy or hostile serving node could send (its signatures and every hash are right; only the manifest's content is not).
    /// </summary>
    private async Task<(string Path, byte[] Detached)> WithBlindManifestAsync(Action<JsonObject> edit)
    {
        var package = await WriteAsync();
        return await ResignAsync(package.FilePath, entries => Change(entries, BlindManifestV2.FileName, data =>
        {
            var json = JsonNode.Parse(data)!.AsObject();
            edit(json);
            return Encoding.UTF8.GetBytes(json.ToJsonString());
        }));
    }

    private static JsonObject OtherPeer(JsonObject manifest) => manifest["whitelist"]!.AsArray().Last()!.AsObject();

    private static JsonObject StandingEvent(string type, string payload, long lamport) =>
        JsonSerializer.SerializeToNode(new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = lamport, EventType = type, Payload = payload,
            Signature = new byte[64], ProtocolVersion = 3, CreatedAt = DateTime.UtcNow,
        })!.AsObject();

    public static IEnumerable<object[]> BadBlindManifests() =>
    [
        ["positions omitted", "*no positions*"],
        ["positions null", "*no positions*"],
        ["a null position", "*position*"],
        ["a position without a node", "*position*"],
        ["a negative position", "*negative*"],
        ["a position twice", "*twice*"],
        ["whitelist omitted", "*no whitelist*"],
        ["a null whitelist row", "*node id*"],
        ["a peer without a node id", "*node id*"],
        ["a peer without a display name", "*display name*"],
        ["a peer key that is not base64", "*public key*"],
        ["a peer key of 31 bytes", "*public key*"],
        ["a peer with a negative Lamport time", "*Lamport*"],
        ["a peer at the Lamport limit", "*Lamport*"],
        ["a peer at the restore grant ceiling", "*Lamport*"],
        ["a peer past the bound a restore accepts", "*Lamport*"],
        ["a standing event at the Lamport limit", "*Lamport*"],
        ["a standing event past the bound a restore accepts", "*Lamport*"],
        ["a standing event that is no event", "*standing*"],
        ["a whitelist_add standing event with an unreadable payload", "*standing*"],
        ["an activation with another shape of key id", "*activation*"],
        ["a negative delta count", "*negative*"],
    ];

    /// <summary>
    /// Spec 8.6 step 2 (review round 2): the receiver validates every field of a signed blind manifest that it will use after
    /// the baseline is adopted, before anything is adopted - a manifest is signed, not safe. Each of these used to pass the
    /// reader and to throw (or to fail halfway) in the seed, after the baseline had been taken.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadBlindManifests))]
    public async Task ABlindManifestWithAMissingOrMalformedField_IsRefused_AlthoughTheServingNodeSignedIt(string what, string message)
    {
        var (path, detached) = await WithBlindManifestAsync(manifest =>
        {
            switch (what)
            {
                case "positions omitted": manifest.Remove("positions"); break;
                case "positions null": manifest["positions"] = null; break;
                case "a null position": manifest["positions"] = new JsonArray((JsonNode?)null); break;
                case "a position without a node":
                    manifest["positions"] = new JsonArray(new JsonObject { ["remote_node_id"] = Guid.Empty.ToString(), ["last_sequence"] = 3 });
                    break;
                case "a negative position":
                    manifest["positions"] = new JsonArray(new JsonObject { ["remote_node_id"] = Guid.NewGuid().ToString(), ["last_sequence"] = -1 });
                    break;
                case "a position twice":
                    var twice = Guid.NewGuid().ToString();
                    manifest["positions"] = new JsonArray(
                        new JsonObject { ["remote_node_id"] = twice, ["last_sequence"] = 1 },
                        new JsonObject { ["remote_node_id"] = twice, ["last_sequence"] = 2 });
                    break;
                case "whitelist omitted": manifest.Remove("whitelist"); break;
                case "a null whitelist row": manifest["whitelist"]!.AsArray().Add(null); break;
                case "a peer without a node id": OtherPeer(manifest)["node_id"] = Guid.Empty.ToString(); break;
                case "a peer without a display name": OtherPeer(manifest)["display_name"] = null; break;
                case "a peer key that is not base64": OtherPeer(manifest)["public_key"] = "%%% not base64 %%%"; break;
                case "a peer key of 31 bytes": OtherPeer(manifest)["public_key"] = Convert.ToBase64String(new byte[31]); break;
                case "a peer with a negative Lamport time": OtherPeer(manifest)["lamport_ts"] = -1; break;
                case "a peer at the Lamport limit": OtherPeer(manifest)["lamport_ts"] = long.MaxValue; break;
                case "a peer at the restore grant ceiling": OtherPeer(manifest)["lamport_ts"] = (1L << 53) - 1; break;
                case "a peer past the bound a restore accepts": OtherPeer(manifest)["lamport_ts"] = BlindManifestV2.MaximumLamport + 1; break;
                case "a standing event at the Lamport limit":
                    manifest["standing"] = new JsonArray(StandingEvent(EventTypes.WhitelistUpdate, "{}", long.MaxValue));
                    break;
                case "a standing event past the bound a restore accepts":
                    manifest["standing"] = new JsonArray(StandingEvent(EventTypes.WhitelistUpdate, "{}", BlindManifestV2.MaximumLamport + 1));
                    break;
                case "a standing event that is no event": manifest["standing"] = new JsonArray((JsonNode?)null); break;
                case "a whitelist_add standing event with an unreadable payload":
                    manifest["standing"] = new JsonArray(StandingEvent(EventTypes.WhitelistAdd, "{ not json", 5));
                    break;
                case "an activation with another shape of key id":
                    manifest["activation"] = new JsonObject { ["activation_id"] = Guid.NewGuid().ToString(), ["key_id"] = "not-a-key-id" };
                    break;
                case "a negative delta count": manifest["delta_count"] = -1; break;
                default: throw new ArgumentException(what);
            }
        });

        var withDetached = () => ReadAsync(path, detached);
        await withDetached.Should().ThrowAsync<InvalidDataException>().WithMessage(message, what);
        var embeddedOnly = () => ReadAsync(path, []);
        await embeddedOnly.Should().ThrowAsync<InvalidDataException>(what);
    }

    [Fact]
    public async Task ABlindManifestRewrittenWithoutAChange_IsStillAccepted_SoTheCasesAboveAreAboutTheirField()
    {
        var (path, detached) = await WithBlindManifestAsync(_ => { });
        var verified = await ReadAsync(path, detached);
        verified.Manifest.Positions.Should().NotBeNull();
        verified.Manifest.Whitelist.Should().HaveCountGreaterThan(1);
    }

    /// <summary>
    /// A package the serving node signed with <paramref name="edit"/> run against its <c>control.db</c> (a copy opened as a plain
    /// SQLite file): what a buggy or hostile serving node could send - every signature and hash is right, only a row is not.
    /// </summary>
    private async Task<(string Path, byte[] Detached)> WithControlDbAsync(Action<SqliteConnection> edit)
    {
        var package = await WriteAsync();
        return await ResignAsync(package.FilePath, entries => Change(entries, BlindManifestV2.ControlDbFileName, data =>
        {
            var file = Path.Combine(_work, "ctl-" + Guid.NewGuid().ToString("N") + ".db");
            File.WriteAllBytes(file, data);
            using (var conn = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                conn.Open();
                edit(conn);
            }
            return File.ReadAllBytes(file);
        }));
    }

    public static IEnumerable<object[]> BadControlRows() =>
    [
        ["a key at the maximum Lamport time", "UPDATE tbl_metadata_key SET lamport_ts = 9223372036854775807", "*tbl_metadata_key*column lamport_ts*"],
        ["a key past the bound a restore accepts", "UPDATE tbl_metadata_key SET lamport_ts = 9007199254740993", "*tbl_metadata_key*column lamport_ts*"],
        ["a key id in capitals", "UPDATE tbl_metadata_key SET key_id = upper('abcdefabcdefabcdefabcdefabcdefab')", "*tbl_metadata_key*column key_id*"],
        ["key material stored as text", "UPDATE tbl_metadata_key SET wrapped = 'not a blob'", "*tbl_metadata_key*column wrapped*"],
        ["an activation naming a key the package lacks",
            "UPDATE tbl_metadata_activation SET key_id = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'", "*tbl_metadata_activation*key the package does not carry*"],
        ["a recovery box with a preset its kind may not have",
            "INSERT INTO tbl_recovery_box VALUES ('11111111-1111-1111-1111-111111111111', 'strong', '22222222-2222-2222-2222-222222222222', "
            + "'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 1, 'd64t3', zeroblob(32), zeroblob(49), zeroblob(12), "
            + "'2026-10-10T00:00:00.0000000Z', 'A', NULL, 5, NULL)", "*tbl_recovery_box*column kdf_preset*"],
        ["a table that is missing", "DROP TABLE tbl_state_anchor", "*tbl_state_anchor is missing*"],
    ];

    /// <summary>
    /// Spec 8.4 and 8.6 step 4 (review round 3): the receiver holds every row of <c>control.db</c> against the rules the live
    /// appliers apply, before anything is adopted. Each of these passed the reader (the table names were right) and was imported
    /// after the baseline was adopted and the legacy content purged - a Lamport time at the limit then failed the first seed's grant.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadControlRows))]
    public async Task AControlDatabaseWithARowNoApplierWouldWrite_IsRefused_AlthoughTheServingNodeSignedIt(string what, string sql, string message)
    {
        var (path, detached) = await WithControlDbAsync(conn => conn.Execute(sql));

        var withDetached = () => ReadAsync(path, detached);
        await withDetached.Should().ThrowAsync<InvalidDataException>().WithMessage(message, what);
        var embeddedOnly = () => ReadAsync(path, []);
        await embeddedOnly.Should().ThrowAsync<InvalidDataException>(what);
    }

    [Fact]
    public async Task AControlDatabaseRewrittenWithoutAChange_IsStillAccepted_SoTheCasesAboveAreAboutTheirRow()
    {
        var (path, detached) = await WithControlDbAsync(_ => { });
        var verified = await ReadAsync(path, detached);
        verified.ControlDbPath.Should().NotBeNull();
    }

    [Fact]
    public async Task AControlDatabaseMadeOfAllowListedViews_IsRefused()
    {
        var (path, detached) = await WithControlDbAsync(ReplaceControlTablesWithViews);

        var read = () => ReadAsync(path, detached);

        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*control*table*");
    }

    private static void ReplaceControlTablesWithViews(SqliteConnection conn)
    {
        foreach (var table in SnapshotTables.Control)
        {
            var columns = conn.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table }).ToList();
            conn.Execute($"DROP TABLE \"{table}\"");
            var projection = string.Join(", ", columns.Select(column => $"NULL AS \"{column}\""));
            conn.Execute($"CREATE VIEW \"{table}\" AS SELECT {projection} WHERE 0");
        }
    }

    /// <summary>The baseline manifest is signed by its producer and read by every receiver: its collections and strings are checked when it is read.</summary>
    [Theory]
    [InlineData("pull_positions omitted")]
    [InlineData("pull_positions null")]
    [InlineData("a null pull position")]
    [InlineData("a negative pull position")]
    [InlineData("key_id omitted")]
    [InlineData("container_sha256 omitted")]
    [InlineData("blob_list_sha256 omitted")]
    [InlineData("a Lamport time past the format-2 bound")]
    [InlineData("a negative blob count")]
    public void ABaselineManifestWithAMissingOrMalformedField_IsRefusedWhenRead(string what)
    {
        var json = JsonNode.Parse(_baseline.ManifestBytes)!.AsObject();
        switch (what)
        {
            case "pull_positions omitted": json.Remove("pull_positions"); break;
            case "pull_positions null": json["pull_positions"] = null; break;
            case "a null pull position": json["pull_positions"] = new JsonArray((JsonNode?)null); break;
            case "a negative pull position":
                json["pull_positions"] = new JsonArray(new JsonObject { ["remote_node_id"] = Guid.NewGuid().ToString(), ["last_sequence"] = -5 });
                break;
            case "key_id omitted": json.Remove("key_id"); break;
            case "container_sha256 omitted": json.Remove("container_sha256"); break;
            case "blob_list_sha256 omitted": json.Remove("blob_list_sha256"); break;
            case "a Lamport time past the format-2 bound": json["lamport_at_cut"] = BlindManifestV2.MaximumLamport + 1; break;
            case "a negative blob count": json["blob_count"] = -1; break;
            default: throw new ArgumentException(what);
        }

        var parse = () => BaselineManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        parse.Should().Throw<InvalidDataException>(what);
        BaselineManifest.Parse(_baseline.ManifestBytes).PullPositionOf(Guid.NewGuid()).Should().Be(0, "the unchanged manifest still reads");
    }

    [Theory]
    [InlineData(BlindManifestV2.MaximumLamport, true)]
    [InlineData(BlindManifestV2.MaximumLamport + 1, false)]
    public void ABaselineManifest_EnforcesTheFormat2LamportBoundary(long lamportAtCut, bool accepted)
    {
        var json = JsonNode.Parse(_baseline.ManifestBytes)!.AsObject();
        json["lamport_at_cut"] = lamportAtCut;
        var parse = () => BaselineManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));

        if (accepted)
            parse.Should().NotThrow();
        else
            parse.Should().Throw<InvalidDataException>();
    }

    private static string Line(SyncEvent e) => Format2Package.ToDeltaLine(e);

    private static List<(string, byte[])> SwapBaseline(List<(string Name, byte[] Data)> entries, BaselineFiles other)
    {
        var result = new List<(string, byte[])>();
        foreach (var (name, data) in entries)
        {
            if (name == Format2Package.BaselineManifestEntry) result.Add((name, other.ManifestBytes));
            else if (name == Format2Package.BaselineSignatureEntry) result.Add((name, other.Signature));
            else if (name == Format2Package.BaselineBlobListEntry)
                result.Add((name, File.ReadAllBytes(Path.Combine(other.Directory, BaselineManifest.BlobListFileName))));
            else if (name == Format2Package.BaselineContainerEntry)
                result.Add((name, File.ReadAllBytes(Path.Combine(other.Directory, BaselineManifest.ContainerFileName))));
            else if (name == BlindManifestV2.FileName)
                result.Add((name, (BlindManifestV2.Parse(data) with { BaselineId = other.Manifest.BaselineId }).ToBytes()));
            else result.Add((name, data));
        }
        return result;
    }

    /// <summary>
    /// Rewrites a package the way a serving node that does not check its own output would: the entries changed by
    /// <paramref name="change"/>, the outer manifest listing their hashes, both signatures made with the serving node's key.
    /// </summary>
    private async Task<(string Path, byte[] Detached)> ResignAsync(string archive, Func<List<(string Name, byte[] Data)>, List<(string, byte[])>> change)
    {
        var entries = Format2TestKit.ReadEntries(archive);
        var outerOld = entries.Single(e => e.Name == Format2Package.ManifestFileName).Data;
        var inner = change(entries.Where(e => e.Name is not (Format2Package.ManifestFileName or Format2Package.ManifestSignatureFileName)).ToList());
        var outer = System.Text.Json.Nodes.JsonNode.Parse(outerOld)!.AsObject();
        var files = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, data) in inner.OrderBy(e => e.Item1, StringComparer.Ordinal))
            files[name] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
        outer["files"] = files;
        var outerBytes = Encoding.UTF8.GetBytes(outer.ToJsonString());
        var embedded = Ed25519Signer.Sign(_node.PrivateKey, Format2Package.EmbeddedSignaturePayload(outerBytes));
        var path = Path.Combine(_work, "resigned-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        Format2TestKit.WriteEntries(path, [.. inner, (Format2Package.ManifestFileName, outerBytes), (Format2Package.ManifestSignatureFileName, embedded)]);
        var detached = Ed25519Signer.Sign(_node.PrivateKey, await Format2Package.DetachedSignaturePayloadAsync(outerBytes, path));
        return (path, detached);
    }

    private static List<(string, byte[])> Change(List<(string Name, byte[] Data)> entries, string name, Func<byte[], byte[]> change) =>
        entries.Select(e => e.Name == name ? (e.Name, change((byte[])e.Data.Clone())) : (e.Name, e.Data)).ToList();
}
