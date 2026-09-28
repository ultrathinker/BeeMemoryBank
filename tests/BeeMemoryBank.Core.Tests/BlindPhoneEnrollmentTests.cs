using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The Windows half of pairing an Android blind node: from the phone's code to the row, the sealed
/// secret and the answer — and the two halves agree (the phone accepts the answer, and the key Windows
/// seals opens the phone's backups).
/// </summary>
public class BlindPhoneEnrollmentTests
{
    private static readonly string Pin = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Prepare_GivesAPlainPeer_TheSecretToSeal_AndACodeThePhoneAccepts()
    {
        var secret = BlindPairingSecret.New();
        var (key, _) = Ed25519Signer.GenerateKeyPair();
        var backupKey = RandomNumberGenerator.GetBytes(32);
        var phone = new BlindPhoneCode(BlindNodeId.NewId(), key, secret, backupKey, "Old phone");
        var hubId = Guid.NewGuid();
        var hubKey = RandomNumberGenerator.GetBytes(32);

        var e = BlindPhoneEnrollment.Prepare(phone.ToString(), "https://hub.test:5300", hubId, Pin,
            hubKey, DateTime.UtcNow, out var error)!;

        error.Should().BeNull();
        e.Entry.NodeId.Should().Be(phone.NodeId);
        e.Entry.Ed25519PublicKey.Should().Equal(key);
        e.Entry.IsSuperadmin.Should().BeFalse("a blind node is never a superadmin");
        e.SealedSecretName.Should().Be($"android-backup:{phone.NodeId}");
        var seal = e.Seal;
        seal.BackupKey.Should().Equal(backupKey, "the key Windows seals must open the phone's backups");
        seal.ProducerNodeId.Should().Be(hubId, "a restore takes the package's producer from the seal, not the file");
        seal.ProducerPublicKey.Should().Equal(hubKey);
        seal.ProducerAddress.Should().Be("https://hub.test:5300");
        seal.ProducerTlsSpki.Should().Be(Pin);
        BlindPhoneBackupSeal.TryDecode(seal.Encode(), out _).Should().BeFalse("unsigned until the pairing node signs it");
        e.CallCode.NodeId.Should().Be(hubId);
        e.CallCode.IsAuthenticBy(secret).Should().BeTrue("the phone must accept the answer");
    }

    [Fact]
    public void Prepare_RefusesAnythingButAPhoneCode()
    {
        BlindPhoneEnrollment.Prepare("bmb-join:?a=https%3A%2F%2Fh%3A1&s=x", "https://hub:1", Guid.NewGuid(), Pin,
            new byte[32], DateTime.UtcNow, out var error).Should().BeNull();
        error.Should().NotBeNull();
    }
}
