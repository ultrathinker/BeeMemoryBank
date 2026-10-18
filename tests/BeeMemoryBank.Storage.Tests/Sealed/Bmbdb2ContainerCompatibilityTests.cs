using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// BMB-304 fix2 (c): old-reader compatibility of the snapshot container. The container is encrypted in place now (one buffer), but its bytes are the format every
/// 2.5/2.6 node reads: a container the CURRENT code writes opens with the reader of the tag v2.6.1 (<see cref="Bmbdb2ContainerV261"/>, a frozen copy of that release),
/// and the other way round. A change of the layout, the key derivation or the AAD fails here before a snapshot made by this version meets an older node.
/// </summary>
public sealed class Bmbdb2ContainerCompatibilityTests
{
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);

    public static IEnumerable<object[]> Sizes() => new[] { 0, 1, 4096, 100_000, (1 << 20) + 123 }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task A_container_the_current_code_writes_opens_with_the_reader_of_v2_6_1(int size)
    {
        var image = RandomNumberGenerator.GetBytes(size);

        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        var opened = await Bmbdb2ContainerV261.DecryptAsync(new MemoryStream(container), container.Length, Dek);

        opened.Should().Equal(image);
        Bmbdb2Container.IsContainer(container).Should().BeTrue();
        System.Text.Encoding.ASCII.GetString(container, 0, 6).Should().Be("BMBDB2");
        container.Length.Should().Be(Bmbdb2Container.OverheadV2 + size, "the layout is magic, salt, iv, tag and the ciphertext of the same length");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task A_container_the_reader_of_v2_6_1_wrote_opens_with_the_current_code(int size)
    {
        var image = RandomNumberGenerator.GetBytes(size);

        var container = await Bmbdb2ContainerV261.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        using var opened = await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek);

        opened.Span.ToArray().Should().Equal(image);
    }

    [Fact]
    public async Task A_legacy_BMBDB1_container_is_read_by_both()
    {
        var image = RandomNumberGenerator.GetBytes(10_000);
        var iv = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var body = (byte[])image.Clone();
        using (var gcm = new AesGcm(Dek, 16)) gcm.Encrypt(iv, body, body, tag, "bmb-snap-db-v1"u8);
        var container = new byte[6 + 12 + 16 + body.Length];
        "BMBDB1"u8.CopyTo(container);
        iv.CopyTo(container, 6);
        tag.CopyTo(container, 18);
        body.CopyTo(container, 34);

        (await Bmbdb2ContainerV261.DecryptAsync(new MemoryStream(container), container.Length, Dek)).Should().Equal(image);
        using var opened = await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek);
        opened.Span.ToArray().Should().Equal(image);
    }

    [Theory]
    [InlineData(40)]   // inside the tag
    [InlineData(60)]   // inside the ciphertext
    public async Task A_changed_byte_is_refused_by_both_readers_alike(int offset)
    {
        var image = RandomNumberGenerator.GetBytes(4096);
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        container[offset] ^= 0xFF;

        var old = async () => await Bmbdb2ContainerV261.DecryptAsync(new MemoryStream(container), container.Length, Dek);
        var current = async () => await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek);

        await old.Should().ThrowAsync<CryptographicException>();
        await current.Should().ThrowAsync<CryptographicException>();
    }
}
