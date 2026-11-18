using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The sealed door (door format v2): fixed vectors computed independently (DoorBoxVectors.json, Python <c>cryptography</c>), tamper of every
/// field, the kind binding, malformed lengths and versions, low-order keys and the zeroing of intermediate key buffers.
/// </summary>
public class DoorBoxTests
{
    private sealed record Vector(string Name, byte[] Kek, byte[] Salt, string Kind, byte[] Eph, byte[] Nonce, byte[] Dek, byte[] DoorSk, byte[] DoorPk, byte[] Box);

    private static IReadOnlyList<Vector> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DoorBoxVectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        static byte[] H(JsonElement e, string n) => Convert.FromHexString(e.GetProperty(n).GetString()!);
        return doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!, H(v, "kek"), H(v, "salt"), v.GetProperty("kind").GetString()!, H(v, "ephemeral_secret"),
            H(v, "nonce"), H(v, "dek"), H(v, "door_secret_key"), H(v, "door_public_key"), H(v, "box"))).ToList();
    }

    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var v in Vectors()) data.Add(v.Name);
        return data;
    }

    private static Vector V(string name) => Vectors().Single(v => v.Name == name);

    [Fact]
    public void The_vector_file_has_every_door_kind_it_claims()
    {
        Vectors().Select(v => v.Kind).Should().Contain(["user", "recovery", "server_auto_unlock", "agent", "quick_unlock"]);
        Vectors().Should().OnlyContain(v => v.Box.Length == DoorBox.BoxSize);
        DoorBox.BoxSize.Should().Be(93);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Key_pair_matches_the_vector(string name)
    {
        var v = V(name);
        using var pair = DoorBox.DeriveKeyPair(v.Kek, v.Salt.Length == 0 ? null : v.Salt);
        pair.SecretKey.Should().Equal(v.DoorSk);
        pair.PublicKey.Should().Equal(v.DoorPk);
        DoorBox.PublicKeyOf(v.DoorSk).Should().Equal(v.DoorPk);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Seal_matches_the_vector_byte_for_byte(string name)
    {
        var v = V(name);
        DoorBox.Seal(v.DoorPk, v.Kind, v.Dek, v.Eph, v.Nonce).Should().Equal(v.Box);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Open_of_the_vector_gives_the_dek(string name)
    {
        var v = V(name);
        DoorBox.TryOpen(v.DoorSk, v.Kind, v.Box).Should().Equal(v.Dek);
        DoorBox.TryOpenWithKek(v.Kek, v.Salt, v.Kind, v.Box).Should().Equal(v.Dek);
    }

    [Fact]
    public void Random_seal_round_trips_and_differs_each_time()
    {
        var kek = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);
        var dek = RandomNumberGenerator.GetBytes(32);
        using var pair = DoorBox.DeriveKeyPair(kek, salt);
        var a = DoorBox.Seal(pair.PublicKey, "user", dek);
        var b = DoorBox.Seal(pair.PublicKey, "user", dek);
        a.Should().NotEqual(b);
        DoorBox.TryOpen(pair.SecretKey, "user", a).Should().Equal(dek);
        DoorBox.TryOpen(pair.SecretKey, "user", b).Should().Equal(dek);
    }

    [Fact]
    public void A_flipped_bit_in_any_byte_of_the_box_is_refused()
    {
        var v = V("user-argon-salt");
        for (var i = 0; i < v.Box.Length; i++)
        {
            var tampered = (byte[])v.Box.Clone();
            tampered[i] ^= 0x01;
            DoorBox.TryOpen(v.DoorSk, v.Kind, tampered).Should().BeNull($"byte {i} ({FieldOf(i)}) was changed");
        }
    }

    private static string FieldOf(int i) => i switch
    {
        0 => "version",
        < 33 => "ephemeral public key",
        < 45 => "nonce",
        < 77 => "ciphertext",
        _ => "tag",
    };

    [Fact]
    public void Another_kind_opens_nothing()
    {
        var v = V("user-argon-salt");
        foreach (var kind in new[] { "recovery", "password", "os_auto_unlock", "server_auto_unlock", "quick_unlock", "agent", "User" })
            DoorBox.TryOpen(v.DoorSk, kind, v.Box).Should().BeNull(kind);
    }

    [Fact]
    public void Another_door_secret_or_salt_or_kek_opens_nothing()
    {
        var v = V("user-argon-salt");
        var otherSalt = (byte[])v.Salt.Clone();
        otherSalt[0] ^= 1;
        var otherKek = (byte[])v.Kek.Clone();
        otherKek[31] ^= 1;
        DoorBox.TryOpenWithKek(v.Kek, otherSalt, v.Kind, v.Box).Should().BeNull();
        DoorBox.TryOpenWithKek(otherKek, v.Salt, v.Kind, v.Box).Should().BeNull();
        DoorBox.TryOpenWithKek(v.Kek, null, v.Kind, v.Box).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(92)]
    [InlineData(94)]
    [InlineData(126)]
    public void A_box_of_the_wrong_length_is_refused(int length)
    {
        var v = V("user-argon-salt");
        var box = new byte[length];
        Array.Copy(v.Box, box, Math.Min(length, v.Box.Length));
        DoorBox.IsWellFormed(box).Should().BeFalse();
        DoorBox.TryOpen(v.DoorSk, v.Kind, box).Should().BeNull();
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x03)]
    [InlineData(0xFF)]
    public void A_box_with_another_version_byte_is_refused(byte version)
    {
        var v = V("user-argon-salt");
        var box = (byte[])v.Box.Clone();
        box[0] = version;
        DoorBox.IsWellFormed(box).Should().BeFalse();
        DoorBox.TryOpen(v.DoorSk, v.Kind, box).Should().BeNull();
    }

    // Low-order points of Curve25519 (u = 0, u = 1 and the order-8 point): the shared secret would be all zero.
    public static TheoryData<string> LowOrderPoints() => new()
    {
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0100000000000000000000000000000000000000000000000000000000000000",
        "e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800",
        "5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157",
        "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
    };

    [Theory]
    [MemberData(nameof(LowOrderPoints))]
    public void Sealing_to_a_low_order_door_key_is_refused(string hex)
    {
        var act = () => DoorBox.Seal(Convert.FromHexString(hex), "user", RandomNumberGenerator.GetBytes(32));
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [MemberData(nameof(LowOrderPoints))]
    public void A_box_with_a_low_order_ephemeral_key_is_refused(string hex)
    {
        var v = V("user-argon-salt");
        var box = (byte[])v.Box.Clone();
        Convert.FromHexString(hex).CopyTo(box, 1);
        DoorBox.TryOpen(v.DoorSk, v.Kind, box).Should().BeNull();
    }

    [Fact]
    public void Bad_arguments_are_refused()
    {
        var v = V("user-argon-salt");
        ((Action)(() => DoorBox.Seal(new byte[31], "user", v.Dek))).Should().Throw<ArgumentException>();
        ((Action)(() => DoorBox.Seal(v.DoorPk, "user", new byte[16]))).Should().Throw<ArgumentException>();
        ((Action)(() => DoorBox.Seal(v.DoorPk, "", v.Dek))).Should().Throw<ArgumentException>();
        ((Action)(() => DoorBox.Seal(v.DoorPk, "us er", v.Dek))).Should().Throw<ArgumentException>();
        ((Action)(() => DoorBox.DeriveKeyPair([], null))).Should().Throw<ArgumentException>();
        DoorBox.TryOpen(new byte[31], v.Kind, v.Box).Should().BeNull();
    }

    [Fact]
    public void Seal_and_open_zero_every_intermediate_key_buffer()
    {
        var v = V("agent");
        var tracked = new List<byte[]>();
        DoorBox.TrackedIntermediates = tracked;
        try
        {
            DoorBox.Seal(v.DoorPk, v.Kind, v.Dek, v.Eph, v.Nonce);
            DoorBox.Seal(v.DoorPk, v.Kind, v.Dek);
            DoorBox.TryOpen(v.DoorSk, v.Kind, v.Box).Should().NotBeNull();
            var tampered = (byte[])v.Box.Clone();
            tampered[^1] ^= 1;
            DoorBox.TryOpen(v.DoorSk, v.Kind, tampered).Should().BeNull();
        }
        finally
        {
            DoorBox.TrackedIntermediates = null;
        }
        tracked.Should().HaveCountGreaterThanOrEqualTo(8);
        tracked.Should().OnlyContain(b => b.All(x => x == 0), "every shared secret, box key and ephemeral secret is zeroed before return");
        v.Eph.Should().NotBeEquivalentTo(new byte[32], "the caller's ephemeral secret is copied, not cleared");
    }

    [Fact]
    public void Disposing_a_key_pair_zeroes_the_secret()
    {
        var v = V("recovery");
        var pair = DoorBox.DeriveKeyPair(v.Kek, v.Salt);
        var secret = pair.SecretKey;
        pair.Dispose();
        secret.Should().OnlyContain(b => b == 0);
    }
}
