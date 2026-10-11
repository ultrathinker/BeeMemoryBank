using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>The BMBDB1/BMBDB2 container (docs/design/step-b-spec.md 8.3, 8.4): the layout every reader of 2.5 and 2.6 understands.</summary>
public sealed class Bmbdb2ContainerTests
{
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);

    private static byte[] Image(int length = 20_000) => RandomNumberGenerator.GetBytes(length);

    [Fact]
    public async Task A_BMBDB2_container_has_the_documented_layout_and_decrypts_to_the_image()
    {
        var image = Image();

        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);

        container.Length.Should().Be(Bmbdb2Container.OverheadV2 + image.Length);
        container.AsSpan(0, 6).SequenceEqual("BMBDB2"u8).Should().BeTrue();
        Bmbdb2Container.IsContainer(container).Should().BeTrue();
        container.AsSpan(Bmbdb2Container.OverheadV2).SequenceEqual(image).Should().BeFalse("the image is encrypted");
        (await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, Dek)).Should().Equal(image);
    }

    [Fact]
    public async Task Two_containers_of_the_same_image_differ_because_salt_and_iv_are_random()
    {
        var image = Image(1000);

        var a = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        var b = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);

        a.Should().NotEqual(b);
        a.AsSpan(6, 16).SequenceEqual(b.AsSpan(6, 16)).Should().BeFalse("the salt");
    }

    [Theory]
    [InlineData(3)]    // the magic
    [InlineData(10)]   // the salt
    [InlineData(25)]   // the iv
    [InlineData(40)]   // the tag
    [InlineData(200)]  // the ciphertext
    public async Task A_flipped_byte_anywhere_and_a_wrong_key_are_refused(int offset)
    {
        var image = Image();
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        var damaged = (byte[])container.Clone();
        damaged[offset] ^= 0x01;

        var flipped = async () => await Bmbdb2Container.DecryptAsync(new MemoryStream(damaged), damaged.Length, Dek);
        var wrong = async () => await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, RandomNumberGenerator.GetBytes(32));

        if (offset < 6) await flipped.Should().ThrowAsync<InvalidDataException>();
        else await flipped.Should().ThrowAsync<CryptographicException>();
        await wrong.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task A_BMBDB1_container_of_an_older_node_is_read_too()
    {
        // BMBDB1: "BMBDB1" || iv(12) || tag(16) || ciphertext, AES-256-GCM under the master DEK itself, AAD "bmb-snap-db-v1".
        var image = Image(5000);
        var iv = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[image.Length];
        using (var gcm = new AesGcm(Dek, 16)) gcm.Encrypt(iv, image, ciphertext, tag, "bmb-snap-db-v1"u8);
        var v1 = "BMBDB1"u8.ToArray().Concat(iv).Concat(tag).Concat(ciphertext).ToArray();

        (await Bmbdb2Container.DecryptAsync(new MemoryStream(v1), v1.Length, Dek)).Should().Equal(image);
        Bmbdb2Container.IsContainer(v1).Should().BeTrue();
    }

    [Fact]
    public async Task Something_that_is_not_a_container_and_an_image_over_the_limit_are_refused()
    {
        var plain = Image(100);
        var notContainer = async () => await Bmbdb2Container.DecryptAsync(new MemoryStream(plain), plain.Length, Dek);
        await notContainer.Should().ThrowAsync<InvalidDataException>();
        Bmbdb2Container.IsContainer(plain).Should().BeFalse();
        Bmbdb2Container.IsContainer("SQLite format 3\0"u8).Should().BeFalse();

        var tooLarge = async () => await Bmbdb2Container.EncryptAsync(Stream.Null, Bmbdb2Container.MaxImageBytes + 1, Dek);
        await tooLarge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 GB*");

        var shortKey = async () => await Bmbdb2Container.EncryptAsync(new MemoryStream(plain), plain.Length, new byte[16]);
        await shortKey.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_image_of_a_sealed_file_goes_straight_into_a_container_without_a_plain_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_container_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = await SealedFixtures.CreateSealedAsync(dir, "vault.db", rows: 100);
            await using var reader = PlainImageReader.Open(db.Path, db.Key);

            var container = await Bmbdb2Container.EncryptAsync(reader, reader.Length, Dek);

            var image = await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, Dek);
            image.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8).Should().BeTrue();
            image.Length.Should().Be((int)new FileInfo(db.Path).Length);
            Directory.GetFiles(dir).Select(Path.GetFileName).Should().OnlyContain(n => n == "vault.db", "no other file was written");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
