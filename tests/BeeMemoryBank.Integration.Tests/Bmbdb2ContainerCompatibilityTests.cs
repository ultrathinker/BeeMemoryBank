using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The container the page codec path writes (<see cref="Bmbdb2Container"/>, used by the CLI on a sealed vault and by the producers of
/// docs/design/step-b-spec.md 8.3) and the one <c>SnapshotService</c> has always written are the same format, byte for byte in both directions.
/// </summary>
public sealed class Bmbdb2ContainerCompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_container_compat_" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(32);

    public Bmbdb2ContainerCompatibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task SnapshotService_decrypts_a_container_the_codec_path_wrote()
    {
        var image = RandomNumberGenerator.GetBytes(30_000);
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, _dek);
        var path = Path.Combine(_dir, "from-codec.bin");
        await File.WriteAllBytesAsync(path, container);

        await SnapshotService.DecryptDbFileAsync(path, _dek);

        (await File.ReadAllBytesAsync(path)).Should().Equal(image);
    }

    [Fact]
    public async Task The_codec_path_decrypts_a_container_SnapshotService_wrote()
    {
        var image = RandomNumberGenerator.GetBytes(30_000);
        var path = Path.Combine(_dir, "from-service.bin");
        await File.WriteAllBytesAsync(path, image);

        await SnapshotService.EncryptDbFileAsync(path, _dek);
        var container = await File.ReadAllBytesAsync(path);

        container.AsSpan(0, 6).SequenceEqual("BMBDB2"u8).Should().BeTrue();
        (await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, _dek)).Should().Equal(image);
        SnapshotService.IsDbEncrypted(container).Should().Be(Bmbdb2Container.IsContainer(container)).And.Be(true);
        container.Length.Should().Be(Bmbdb2Container.OverheadV2 + image.Length);
    }
}
