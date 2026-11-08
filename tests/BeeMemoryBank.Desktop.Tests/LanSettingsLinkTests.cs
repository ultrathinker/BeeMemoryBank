using System;
using System.IO;
using System.Linq;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The page the shell opens from the LAN prompt, the tray line and the one-time notice is the card with the networks: Admin &gt; Devices &gt; Network
/// settings, at the "Sync with your devices on this network" card. The old <c>/Admin/DevicesOnMyNetwork</c> redirects to another card of that page,
/// so the shell names the new place itself; this test fails when the page or its card is renamed.
/// </summary>
public sealed class LanSettingsLinkTests
{
    [Fact]
    public void TheShellOpensTheSyncCardOfNetworkSettings_AndThePageHasIt()
    {
        LanNetworkPrompt.SettingsPath.Should().Be("/Admin/NetworkSettings#lan-sync");

        var page = File.ReadAllText(RepoFile("server", "BeeMemoryBank.Web", "Pages", "Admin", "NetworkSettings.cshtml"));
        page.Split('\n')[0].Trim().Should().Be("@page", "the page's route is its path, /Admin/NetworkSettings");
        page.Should().Contain("id=\"lan-sync\"", "the card the shell opens");
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the repository");
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
