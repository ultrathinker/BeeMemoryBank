using BeeMemoryBank.Hosting.AspNetCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web layer's data-protection key ring, which is what antiforgery tokens and the
/// <c>bee_session</c> auth cookie are protected with. Left at its default, ASP.NET Core puts it
/// somewhere outside the data directory — a user-profile path when one exists, nothing at all
/// when it doesn't — so it is regenerated whenever that path is missing or wiped, and a user
/// with a page already open then gets one unexplained failure per deployment.
/// <see cref="DataProtectionExtensions.AddPersistedDataProtection"/> — the exact call
/// BeeMemoryBank.Web's Program.cs makes — puts it under the data directory instead.
///
/// These tests build the real registration over a temp directory and then throw the whole
/// provider away, which is what a restart looks like from the key ring's point of view: a fresh
/// provider over the same directory is the restarted process, and a fresh provider over an EMPTY
/// directory is a container recreated without the volume — the case that must still fail, so the
/// round-trip above is proved to come from the persisted directory and not from some ambient
/// machine-wide store.
/// </summary>
public class WebSessionKeyRingPersistenceTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "bmb_keyring_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            try { Directory.Delete(_dataPath, recursive: true); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>One "process": a provider wired exactly the way Web's Program.cs wires it.</summary>
    private static ServiceProvider StartProcess(string dataPath) =>
        new ServiceCollection()
            .AddLogging()
            .AddPersistedDataProtection(dataPath, "BeeMemoryBank.Web")
            .BuildServiceProvider();

    private static IDataProtector Protector(IServiceProvider sp) =>
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("antiforgery");

    [Fact]
    public void KeyRing_IsWrittenUnderTheConfiguredDataPath()
    {
        // The data directory is the mounted volume in Docker; anywhere else and the ring dies
        // with the container, which is the whole point of persisting it.
        Directory.Exists(_dataPath).Should().BeFalse("the data directory does not exist yet");

        using var process = StartProcess(_dataPath);
        Protector(process).Protect("payload");

        var keyRingPath = DataProtectionExtensions.KeyRingPath(_dataPath);
        keyRingPath.Should().StartWith(_dataPath);
        Directory.GetFiles(keyRingPath, "key-*.xml").Should().NotBeEmpty(
            "the ring must land in the data directory, created if it was missing");
    }

    [Fact]
    public void TokenProtectedBeforeARestart_IsStillReadableAfterIt()
    {
        string token;
        using (var before = StartProcess(_dataPath))
        {
            token = Protector(before).Protect("antiforgery-token-payload");
        }

        using var after = StartProcess(_dataPath);

        Protector(after).Unprotect(token).Should().Be("antiforgery-token-payload",
            "a token minted before a restart must survive it — otherwise every open page fails once per deployment");
    }

    [Fact]
    public void AFreshContainerWithoutTheVolume_CannotReadTheOldToken()
    {
        // Counterpart to the test above: proves it is the persisted directory doing the work,
        // not an ambient machine-wide key store that would have made it pass regardless.
        string token;
        using (var before = StartProcess(_dataPath))
        {
            token = Protector(before).Protect("antiforgery-token-payload");
        }

        var emptyDataPath = _dataPath + "_recreated";
        try
        {
            using var elsewhere = StartProcess(emptyDataPath);

            var read = () => Protector(elsewhere).Unprotect(token);

            read.Should().Throw<Exception>();
        }
        finally
        {
            try { Directory.Delete(emptyDataPath, recursive: true); } catch { }
        }
    }
}
