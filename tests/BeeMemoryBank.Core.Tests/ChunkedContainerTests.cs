using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The bounded-memory test counts the allocations of the whole process around an async seal and open, which are only the container's own when
/// no other test runs at the same time (the thread-local counter loses the continuation that hops threads): this collection runs alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChunkedContainerAllocation
{
    public const string Name = "ChunkedContainerAllocation";
}

/// <summary>BMBCH1, the chunked container of baselines and encrypted joins (docs/design/step-a-spec.md 7.1, 16.4 item 1).</summary>
[Collection(ChunkedContainerAllocation.Name)]
public class ChunkedContainerTests
{
    private const int Chunk = 4096;
    private static readonly byte[] Root = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private const string Label = "mek:00112233445566778899aabbccddeeff";

    private static readonly ChunkKeyDeriver RootDeriver = (info, salt) =>
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Root, key, salt, Encoding.UTF8.GetBytes(info));
        return key;
    };

    private static byte[] Data(int length, int seed = 7)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static async Task<byte[]> SealAsync(byte[] plain, string purpose = ChunkedContainer.PurposeBaseline, string label = Label, byte[]? root = null, int chunk = Chunk)
    {
        using var output = new MemoryStream();
        await ChunkedContainer.EncryptAsync(new MemoryStream(plain), output, purpose, label, root ?? Root, chunk);
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] container, string purpose = ChunkedContainer.PurposeBaseline, byte[]? root = null)
    {
        using var output = new MemoryStream();
        await ChunkedContainer.DecryptAsync(new MemoryStream(container), output, purpose, h => h.Key == Label ? (root ?? Root) : null);
        return output.ToArray();
    }

    /// <summary>Offsets of the chunk length prefixes, and the offset one past the end, of a well-formed container.</summary>
    private static List<(int Start, int Length)> Chunks(byte[] container)
    {
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8));
        var at = 12 + headerLength;
        var list = new List<(int, int)>();
        while (at < container.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(at));
            list.Add((at, 4 + length));
            at += 4 + length;
        }
        return list;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData(3 * Chunk + 17)]
    public async Task Round_trips_every_boundary_size(int length)
    {
        var plain = Data(length);
        var container = await SealAsync(plain);

        (await OpenAsync(container)).Should().Equal(plain);
        Chunks(container).Should().HaveCount(Math.Max(1, (length + Chunk - 1) / Chunk));
    }

    [Fact]
    public async Task Empty_input_is_one_empty_final_chunk()
    {
        var container = await SealAsync([]);
        var chunks = Chunks(container);
        chunks.Should().ContainSingle();
        chunks[0].Length.Should().Be(4 + 16);
        (await OpenAsync(container)).Should().BeEmpty();
    }

    [Fact]
    public async Task Header_names_purpose_key_and_chunk_size_and_hides_the_plaintext()
    {
        var canary = MetadataCanaries.NonAscii("hdr");
        var plain = Encoding.UTF8.GetBytes(canary + new string('x', 3 * Chunk));
        var container = await SealAsync(plain);

        container.AsSpan(0, 8).SequenceEqual("BMBCH1\0\0"u8).Should().BeTrue();
        ChunkedContainer.HasMagic(container).Should().BeTrue();
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8));
        var header = Encoding.UTF8.GetString(container, 12, headerLength);
        header.Should().Contain("\"v\":1").And.Contain("\"purpose\":\"baseline\"").And.Contain($"\"key\":\"{Label}\"").And.Contain($"\"chunk\":{Chunk}");
        MetadataCanaries.Scan(container, [canary], "container").Should().BeEmpty();
        // control: the scanner does find it in the plain bytes
        MetadataCanaries.Scan(plain, [canary], "plain").Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_deriver_that_never_shows_the_root_key_gives_a_container_the_root_key_opens_and_back()
    {
        var plain = Data(3 * Chunk + 1);
        using var viaDeriver = new MemoryStream();
        await ChunkedContainer.EncryptWithAsync(new MemoryStream(plain), viaDeriver, ChunkedContainer.PurposeBaseline, Label, RootDeriver, Chunk);

        (await OpenAsync(viaDeriver.ToArray())).Should().Equal(plain);
        using var back = new MemoryStream();
        await ChunkedContainer.DecryptWithAsync(new MemoryStream(await SealAsync(plain)), back, ChunkedContainer.PurposeBaseline, _ => RootDeriver);
        back.ToArray().Should().Equal(plain);
    }

    [Fact]
    public async Task A_deriver_that_throws_or_is_missing_is_a_refusal_not_a_crash()
    {
        var container = await SealAsync(Data(Chunk));
        using var sink = new MemoryStream();
        await FluentActions.Awaiting(() => ChunkedContainer.DecryptWithAsync(new MemoryStream(container), sink, ChunkedContainer.PurposeBaseline, _ => (info, salt) => throw new InvalidOperationException("locked")))
            .Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => ChunkedContainer.DecryptWithAsync(new MemoryStream(container), sink, ChunkedContainer.PurposeBaseline, _ => null))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Same_input_twice_gives_different_containers()
    {
        var plain = Data(2 * Chunk);
        (await SealAsync(plain)).Should().NotEqual(await SealAsync(plain));
    }

    [Fact]
    public async Task Truncation_at_every_chunk_boundary_is_refused()
    {
        var container = await SealAsync(Data(4 * Chunk + 5));
        var chunks = Chunks(container);
        chunks.Should().HaveCount(5);

        for (var keep = 0; keep < chunks.Count; keep++)
        {
            var cut = keep == 0 ? 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8)) : chunks[keep - 1].Start + chunks[keep - 1].Length;
            var truncated = container[..cut];
            await FluentActions.Awaiting(() => OpenAsync(truncated)).Should().ThrowAsync<InvalidDataException>($"cut after {keep} chunks");
        }
    }

    [Fact]
    public async Task Truncation_inside_a_chunk_or_a_length_prefix_is_refused()
    {
        var container = await SealAsync(Data(3 * Chunk));
        var chunks = Chunks(container);
        foreach (var cut in new[] { chunks[1].Start + 2, chunks[1].Start + 4 + 10, chunks[2].Start + chunks[2].Length - 1, 7, 11, 12 })
            await FluentActions.Awaiting(() => OpenAsync(container[..cut])).Should().ThrowAsync<InvalidDataException>($"cut at {cut}");
    }

    [Fact]
    public async Task Reordered_chunks_are_refused()
    {
        var container = await SealAsync(Data(3 * Chunk));
        var chunks = Chunks(container);
        var swapped = container.ToArray();
        container.AsSpan(chunks[1].Start, chunks[1].Length).CopyTo(swapped.AsSpan(chunks[0].Start));
        container.AsSpan(chunks[0].Start, chunks[0].Length).CopyTo(swapped.AsSpan(chunks[0].Start + chunks[1].Length));

        await FluentActions.Awaiting(() => OpenAsync(swapped)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_chunk_from_another_container_under_the_same_key_is_refused()
    {
        var one = await SealAsync(Data(3 * Chunk, 1));
        var other = await SealAsync(Data(3 * Chunk, 2));
        var a = Chunks(one);
        var b = Chunks(other);
        var mixed = one.ToArray();
        other.AsSpan(b[1].Start, b[1].Length).CopyTo(mixed.AsSpan(a[1].Start));

        await FluentActions.Awaiting(() => OpenAsync(mixed)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_duplicated_chunk_is_refused()
    {
        var container = await SealAsync(Data(3 * Chunk));
        var chunks = Chunks(container);
        var doubled = container.Concat(container.AsSpan(chunks[1].Start, chunks[1].Length).ToArray()).ToArray();
        await FluentActions.Awaiting(() => OpenAsync(doubled)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Every_header_byte_is_authenticated()
    {
        var container = await SealAsync(Data(Chunk + 3));
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8));
        for (var i = 0; i < 12 + headerLength; i++)
        {
            var broken = container.ToArray();
            broken[i] ^= 0x01;
            await FluentActions.Awaiting(() => OpenAsync(broken)).Should().ThrowAsync<InvalidDataException>($"header byte {i}");
        }
    }

    [Fact]
    public async Task Flipping_any_chunk_byte_is_refused()
    {
        var container = await SealAsync(Data(2 * Chunk + 9));
        var chunks = Chunks(container);
        foreach (var (start, length) in chunks)
            foreach (var offset in new[] { 4, 4 + length / 2, length - 1 })
            {
                var broken = container.ToArray();
                broken[start + offset] ^= 0x80;
                await FluentActions.Awaiting(() => OpenAsync(broken)).Should().ThrowAsync<InvalidDataException>();
            }
    }

    [Fact]
    public async Task A_length_prefix_that_lies_is_refused()
    {
        var container = await SealAsync(Data(2 * Chunk));
        var chunks = Chunks(container);
        foreach (var length in new uint[] { 0, 15, (uint)Chunk + 17, uint.MaxValue })
        {
            var broken = container.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(chunks[0].Start), length);
            await FluentActions.Awaiting(() => OpenAsync(broken)).Should().ThrowAsync<InvalidDataException>($"length {length}");
        }
    }

    [Fact]
    public async Task Trailing_bytes_are_refused()
    {
        var container = await SealAsync(Data(2 * Chunk + 1));
        foreach (var extra in new[] { new byte[] { 0 }, new byte[] { 1, 2, 3 }, new byte[] { 0, 0, 0, 0 }, new byte[16 + 4] })
            await FluentActions.Awaiting(() => OpenAsync([.. container, .. extra])).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_final_chunk_sealed_without_the_final_flag_is_refused()
    {
        // Built by hand with the documented nonce rule, so the only defect is the missing last flag.
        var plain = Data(Chunk + 10);
        var container = await SealAsync(plain);
        var (header, aad, key) = ParseForCrafting(container);
        var rebuilt = CraftContainer(container, header, aad, key, plain, firstIndex: 0, lastFlagOnFinal: false);

        await FluentActions.Awaiting(() => OpenAsync(rebuilt)).Should().ThrowAsync<InvalidDataException>();
        // control: the same crafting with the flag set opens, so the helper itself is right
        (await OpenAsync(CraftContainer(container, header, aad, key, plain, firstIndex: 0, lastFlagOnFinal: true))).Should().Equal(plain);
    }

    [Fact]
    public async Task Wrong_key_wrong_purpose_and_missing_key_are_refused()
    {
        var container = await SealAsync(Data(2 * Chunk));
        var wrong = Root.ToArray();
        wrong[0] ^= 1;

        await FluentActions.Awaiting(() => OpenAsync(container, root: wrong)).Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => OpenAsync(container, purpose: ChunkedContainer.PurposeJoin)).Should().ThrowAsync<InvalidDataException>();
        using var sink = new MemoryStream();
        await FluentActions.Awaiting(() => ChunkedContainer.DecryptAsync(new MemoryStream(container), sink, ChunkedContainer.PurposeBaseline, _ => null))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_join_container_keyed_by_the_dek_label_does_not_open_as_a_baseline_and_vice_versa()
    {
        var dekLabel = ChunkedContainer.DekKeyLabel("abcd1234");
        var join = await SealAsync(Data(Chunk), ChunkedContainer.PurposeJoin, dekLabel);
        using var ok = new MemoryStream();
        var header = await ChunkedContainer.DecryptAsync(new MemoryStream(join), ok, ChunkedContainer.PurposeJoin, h => h.Key == dekLabel ? Root : null);
        header.Key.Should().Be("dek:abcd1234");
        header.Purpose.Should().Be("join");

        await FluentActions.Awaiting(() => OpenAsync(join)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Not_a_container_is_refused_by_magic_and_plain_sqlite_has_no_magic()
    {
        ChunkedContainer.HasMagic("SQLite format 3\0"u8).Should().BeFalse();
        await FluentActions.Awaiting(() => OpenAsync("SQLite format 3\0 and then some bytes"u8.ToArray())).Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => OpenAsync([])).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Header_rules_are_enforced_on_read()
    {
        var container = await SealAsync(Data(Chunk));
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8));

        // a length of 0 or beyond 4096
        foreach (var length in new uint[] { 0, 4097, uint.MaxValue })
        {
            var broken = container.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(8), length);
            await FluentActions.Awaiting(() => OpenAsync(broken)).Should().ThrowAsync<InvalidDataException>($"header length {length}");
        }

        // a header that is valid JSON but breaks a rule, authenticated properly by rebuilding the AAD: still refused by the rule
        foreach (var json in new[]
                 {
                     """{"v":2,"purpose":"baseline","key":"mek:aa","salt":"AAAAAAAAAAAAAAAAAAAAAA==","nonce_prefix":"AAAAAAAAAA==","chunk":4096}""",
                     """{"v":1,"purpose":"baseline","key":"mek:aa","salt":"AAAA","nonce_prefix":"AAAAAAAAAA==","chunk":4096}""",
                     """{"v":1,"purpose":"baseline","key":"mek:aa","salt":"AAAAAAAAAAAAAAAAAAAAAA==","nonce_prefix":"AAAAAAAAAA==","chunk":100}""",
                     """{"v":1,"purpose":"baseline","key":"mek:aa","salt":"AAAAAAAAAAAAAAAAAAAAAA==","nonce_prefix":"AAAAAAAAAA==","chunk":16777217}""",
                     """{"v":1,"purpose":"baseline","key":"mek:aa","salt":"AAAAAAAAAAAAAAAAAAAAAA==","nonce_prefix":"AAAAAAAAAA==","chunk":4096,"extra":1}""",
                     """{"v":1,"purpose":"baseline","key":"mek:aa","salt":"AAAAAAAAAAAAAAAAAAAAAA==","nonce_prefix":"AAAAAAAAAA=="}""",
                     """not json""",
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var crafted = new byte[12 + bytes.Length + 20];
            "BMBCH1\0\0"u8.CopyTo(crafted);
            BinaryPrimitives.WriteUInt32LittleEndian(crafted.AsSpan(8), (uint)bytes.Length);
            bytes.CopyTo(crafted, 12);
            await FluentActions.Awaiting(() => OpenAsync(crafted)).Should().ThrowAsync<InvalidDataException>(json);
        }

        headerLength.Should().BeInRange(1, 4096);
    }

    [Fact]
    public async Task Writer_refuses_bad_arguments()
    {
        using var output = new MemoryStream();
        await FluentActions.Awaiting(() => ChunkedContainer.EncryptAsync(new MemoryStream(), output, "other", Label, Root, Chunk)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => ChunkedContainer.EncryptAsync(new MemoryStream(), output, "baseline", Label, new byte[16], Chunk)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => ChunkedContainer.EncryptAsync(new MemoryStream(), output, "baseline", Label, Root, 100)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => ChunkedContainer.EncryptAsync(new MemoryStream(), output, "baseline", Label, Root, 16 * 1024 * 1024 + 1)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Files_round_trip_and_refuse_to_overwrite()
    {
        var dir = Directory.CreateTempSubdirectory("bmbch1-").FullName;
        try
        {
            var plainPath = Path.Combine(dir, "plain.db");
            var plain = Data(5 * Chunk + 3);
            await File.WriteAllBytesAsync(plainPath, plain);
            var containerPath = Path.Combine(dir, "c.bmbch");
            await ChunkedContainer.EncryptFileAsync(plainPath, containerPath, ChunkedContainer.PurposeBaseline, Label, Root, Chunk);
            var back = Path.Combine(dir, "back.db");
            await ChunkedContainer.DecryptFileAsync(containerPath, back, ChunkedContainer.PurposeBaseline, _ => Root);

            (await File.ReadAllBytesAsync(back)).Should().Equal(plain);
            await FluentActions.Awaiting(() => ChunkedContainer.EncryptFileAsync(plainPath, containerPath, ChunkedContainer.PurposeBaseline, Label, Root, Chunk))
                .Should().ThrowAsync<IOException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the counter bound (spec 7.1, 16.4 item 1) ----

    [Fact]
    public void Nonce_is_prefix_then_big_endian_index_then_the_final_flag()
    {
        var prefix = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        ChunkedContainer.NonceFor(prefix, 0x01020304, last: false).Should().Equal(1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 4, 0);
        ChunkedContainer.NonceFor(prefix, 0x01020304, last: true).Should().Equal(1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 4, 1);
    }

    [Fact]
    public void Nonce_accepts_the_largest_index_and_refuses_one_more_and_negative()
    {
        var prefix = new byte[7];
        ChunkedContainer.NonceFor(prefix, uint.MaxValue, false).Should().Equal(0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0);
        FluentActions.Invoking(() => ChunkedContainer.NonceFor(prefix, 4294967296, false)).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => ChunkedContainer.NonceFor(prefix, 4294967296, true)).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => ChunkedContainer.NonceFor(prefix, -1, false)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Nonces_of_neighbours_and_of_the_two_flags_differ()
    {
        var prefix = RandomNumberGenerator.GetBytes(7);
        var all = new HashSet<string>();
        for (long i = 0; i < 50; i++)
            foreach (var last in new[] { false, true })
                all.Add(Convert.ToHexString(ChunkedContainer.NonceFor(prefix, i, last))).Should().BeTrue();
        all.Add(Convert.ToHexString(ChunkedContainer.NonceFor(prefix, uint.MaxValue, false))).Should().BeTrue();
        all.Add(Convert.ToHexString(ChunkedContainer.NonceFor(prefix, uint.MaxValue - 1, false))).Should().BeTrue();
    }

    [Fact]
    public async Task Writer_near_the_counter_limit_seals_two_chunks_and_refuses_the_third()
    {
        var plain = Data(3 * Chunk);
        using var output = new MemoryStream();

        await FluentActions.Awaiting(() => ChunkedContainer.EncryptCoreAsync(
                new MemoryStream(plain), output, ChunkedContainer.PurposeBaseline, Label, RootDeriver, Chunk, firstChunkIndex: 4294967294, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();

        Chunks(output.ToArray()).Should().HaveCount(2, "the third chunk was refused before it was sealed");
    }

    [Fact]
    public async Task Writer_near_the_limit_accepts_a_two_chunk_input()
    {
        var plain = Data(2 * Chunk);
        using var output = new MemoryStream();
        await ChunkedContainer.EncryptCoreAsync(new MemoryStream(plain), output, ChunkedContainer.PurposeBaseline, Label, RootDeriver, Chunk, 4294967294, CancellationToken.None);
        using var back = new MemoryStream();
        await ChunkedContainer.DecryptCoreAsync(new MemoryStream(output.ToArray()), back, ChunkedContainer.PurposeBaseline, _ => RootDeriver, 4294967294, CancellationToken.None);
        back.ToArray().Should().Equal(plain);
    }

    [Fact]
    public async Task Reader_near_the_limit_refuses_a_third_chunk_before_opening_it()
    {
        // The file is sealed by hand with the index wrapped to its low 32 bits: chunk 2 would open if the reader opened it.
        var plain = Data(3 * Chunk);
        var seed = await SealAsync(plain);
        var (header, aad, key) = ParseForCrafting(seed);
        var crafted = CraftContainer(seed, header, aad, key, plain, firstIndex: 4294967294, lastFlagOnFinal: true, wrap: true);

        using var sink = new MemoryStream();
        await FluentActions.Awaiting(() => ChunkedContainer.DecryptCoreAsync(
                new MemoryStream(crafted), sink, ChunkedContainer.PurposeBaseline, _ => RootDeriver, 4294967294, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
        sink.Length.Should().Be(2 * Chunk, "only the first two chunks were opened");

        Chunks(crafted).Should().HaveCount(3);
    }

    // ---- bounded memory (spec 16.4 item 1) ----

    [Fact]
    public async Task A_64_MiB_input_with_64_KiB_chunks_round_trips_in_bounded_memory()
    {
        const long size = 64L * 1024 * 1024;
        const int chunk = 64 * 1024;
        var dir = Directory.CreateTempSubdirectory("bmbch1-big-").FullName;
        try
        {
            var path = Path.Combine(dir, "big.bmbch");
            var before = GC.GetTotalAllocatedBytes(precise: true);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await ChunkedContainer.EncryptAsync(new PatternSource(size), file, ChunkedContainer.PurposeBaseline, Label, Root, chunk);
            var afterSeal = GC.GetTotalAllocatedBytes(precise: true);

            var sink = new HashingSink();
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await ChunkedContainer.DecryptAsync(file, sink, ChunkedContainer.PurposeBaseline, _ => Root);
            var afterOpen = GC.GetTotalAllocatedBytes(precise: true);

            new FileInfo(path).Length.Should().BeGreaterThan(size);
            sink.Length.Should().Be(size);
            sink.Hash().Should().Equal(PatternSource.HashOf(size));
            (afterSeal - before).Should().BeLessThan(8 * 1024 * 1024, "sealing 64 MiB must not allocate the input");
            (afterOpen - afterSeal).Should().BeLessThan(8 * 1024 * 1024, "opening 64 MiB must not allocate the output");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- crafting helpers: the documented format, written independently of the production code ----

    private static (ChunkedContainerHeader Header, byte[] Aad, byte[] Key) ParseForCrafting(byte[] container)
    {
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8));
        var json = Encoding.UTF8.GetString(container, 12, headerLength);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        var header = new ChunkedContainerHeader(
            r.GetProperty("v").GetInt32(), r.GetProperty("purpose").GetString()!, r.GetProperty("key").GetString()!,
            Convert.FromBase64String(r.GetProperty("salt").GetString()!), Convert.FromBase64String(r.GetProperty("nonce_prefix").GetString()!),
            r.GetProperty("chunk").GetInt32());
        var aad = SHA256.HashData(container.AsSpan(0, 12 + headerLength));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Root, 32, header.Salt, Encoding.UTF8.GetBytes("bmb-chunked-v1:" + header.Purpose));
        return (header, aad, key);
    }

    private static byte[] CraftContainer(byte[] original, ChunkedContainerHeader header, byte[] aad, byte[] key, byte[] plain,
        long firstIndex, bool lastFlagOnFinal, bool wrap = false)
    {
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(8));
        using var output = new MemoryStream();
        output.Write(original, 0, 12 + headerLength);
        using var aes = new AesGcm(key, 16);
        var count = Math.Max(1, (plain.Length + header.ChunkSize - 1) / header.ChunkSize);
        for (var i = 0; i < count; i++)
        {
            var piece = plain.AsSpan(i * header.ChunkSize, Math.Min(header.ChunkSize, plain.Length - i * header.ChunkSize));
            var nonce = new byte[12];
            header.NoncePrefix.CopyTo(nonce, 0);
            var index = firstIndex + i;
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(7, 4), wrap ? (uint)(index & 0xFFFFFFFF) : (uint)index);
            nonce[11] = (i == count - 1 && lastFlagOnFinal) ? (byte)1 : (byte)0;
            var cipher = new byte[piece.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, piece, cipher, tag, aad);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)(piece.Length + 16));
            output.Write(length);
            output.Write(cipher);
            output.Write(tag);
        }
        return output.ToArray();
    }

    /// <summary>A read-only stream of <c>size</c> deterministic bytes that holds none of them.</summary>
    private sealed class PatternSource(long size) : Stream
    {
        private long _position;
        public static byte At(long i) => (byte)((i * 31 + (i >> 8)) & 0xFF);

        public static byte[] HashOf(long size)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            for (long done = 0; done < size;)
            {
                var n = (int)Math.Min(buffer.Length, size - done);
                for (var i = 0; i < n; i++) buffer[i] = At(done + i);
                hash.AppendData(buffer, 0, n);
                done += n;
            }
            return hash.GetHashAndReset();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, size - _position);
            for (var i = 0; i < n; i++) buffer[i] = At(_position + i);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HashingSink : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        public byte[] Hash() => _hash.GetHashAndReset();
        public override void Write(byte[] buffer, int offset, int count) { _hash.AppendData(buffer, offset, count); _length += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); _length += buffer.Length; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Write(buffer.Span); return ValueTask.CompletedTask; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
