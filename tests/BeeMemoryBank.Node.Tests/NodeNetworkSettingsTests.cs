using System;
using System.IO;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Infrastructure.Network;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// The per-profile "Devices on my network" setting: off unless someone switched it on, remembered per data folder, never
/// carried to another computer with a copied vault, and overridden (not erased) by the older BMB_HTTPS_ENABLED=1.
/// </summary>
public class NodeNetworkSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_netsettings_" + Guid.NewGuid().ToString("N"));

    public NodeNetworkSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder of a finished test */ }
    }

    [Fact]
    public void NoFile_MeansOff()
    {
        var store = new NodeNetworkSettingsStore(_dir);

        store.Load().DevicesOnMyNetwork.Should().BeFalse("a node nobody asked to open stays closed");
        store.Current().Should().Be(new NetworkExposure(false, NetworkExposureSource.Off));
    }

    [Fact]
    public void SavedValue_IsReadBack_ByAnotherInstance_AsTheNodeApiAndWebAreOtherProcesses()
    {
        new NodeNetworkSettingsStore(_dir).Save(new NodeNetworkSettings(DevicesOnMyNetwork: true));

        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeTrue();

        new NodeNetworkSettingsStore(_dir).Save(new NodeNetworkSettings(DevicesOnMyNetwork: false));
        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeFalse();
    }

    [Fact]
    public void Save_CreatesTheFolder_AndLeavesNoTemporaryFileBehind()
    {
        var nested = Path.Combine(_dir, "profile", "data");

        new NodeNetworkSettingsStore(nested).Save(new NodeNetworkSettings(true));

        Directory.GetFiles(nested).Select(Path.GetFileName).Should().Equal(NodeNetworkSettingsStore.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"devicesOnMyNetwork\": \"yes\"}")]
    public void ADamagedFile_FailsTowardOff(string content)
    {
        File.WriteAllText(Path.Combine(_dir, NodeNetworkSettingsStore.FileName), content);

        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeFalse();
    }

    [Fact]
    public void AFolderInTheFilesPlace_FailsTowardOff()
    {
        Directory.CreateDirectory(Path.Combine(_dir, NodeNetworkSettingsStore.FileName));

        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, null, false, NetworkExposureSource.Off)]
    [InlineData(true, null, true, NetworkExposureSource.Setting)]
    [InlineData(false, "1", true, NetworkExposureSource.Environment)]
    [InlineData(true, "1", true, NetworkExposureSource.Environment)]
    [InlineData(false, "0", false, NetworkExposureSource.Off)]
    [InlineData(false, "true", false, NetworkExposureSource.Off)]
    [InlineData(true, "0", true, NetworkExposureSource.Setting)]
    public void TheEnvironmentVariable_OnlyEverSwitchesTheListenerOn_ByItsExactValueOne(
        bool setting, string? env, bool enabled, NetworkExposureSource source)
    {
        NetworkExposure.Resolve(new NodeNetworkSettings(setting), env).Should().Be(new NetworkExposure(enabled, source));
    }

    [Fact]
    public void TheFileName_IsOneThatNoVaultCopyCarriesToAnotherComputer()
    {
        // Infrastructure cannot reference AppPaths, so the two spellings are tied here.
        NodeNetworkSettingsStore.FileName.Should().Be(VaultFiles.NetworkSettingsFileName);
        VaultFiles.IsTransient(NodeNetworkSettingsStore.FileName).Should().BeTrue(
            "a vault moved to another computer must start closed there, whatever the old computer chose");

        LanListenerState.FileName.Should().Be(VaultFiles.NetworkListenerStateFileName);
        VaultFiles.IsTransient(LanListenerState.FileName).Should().BeTrue("a copied vault must not arrive claiming its listener is up");
    }

    [Fact]
    public void ListenerState_IsNotListening_ByDefault_AndFollowsWhatTheNodeWrote()
    {
        var state = new LanListenerState(_dir);
        state.IsListening().Should().BeFalse("no file: nothing is up");

        state.Set(true).Should().BeTrue();
        new LanListenerState(_dir).IsListening().Should().BeTrue("the Api is another process reading what the node wrote");
        state.Set(false);
        state.IsListening().Should().BeFalse();
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { LanListenerState.FileName }, "no temporary file is left behind");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[true]")]
    [InlineData("{\"listening\": \"yes\"}")]
    [InlineData("{\"listening\": 1}")]
    public void ListenerState_ADamagedFile_IsNotListening(string content)
    {
        File.WriteAllText(Path.Combine(_dir, LanListenerState.FileName), content);

        new LanListenerState(_dir).IsListening().Should().BeFalse();
    }
}
