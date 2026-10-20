using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-322: what a node says it runs on (a Mac, a Windows PC, a Linux server, a phone, a container) so that the network map can draw it, and how a node that
/// is told remembers it. A closed set of names: a peer's word is read through <see cref="NodePlatform.Normalize"/>, never shown as it came.
/// </summary>
public class NodePlatformTests
{
    [Theory]
    [InlineData(true, false, false, false, true, "android")]    // Android reports itself as Linux too: the phone wins
    [InlineData(false, true, false, false, false, "ios")]
    [InlineData(false, true, true, false, false, "ios")]        // iOS reports itself as macOS too
    [InlineData(false, false, true, false, false, "macos")]
    [InlineData(false, false, false, true, false, "windows")]
    [InlineData(false, false, false, false, true, "linux")]
    [InlineData(false, false, false, false, false, null)]
    public void TheRuntimeFlagsBecomeOneName_AndAPhoneWinsOverTheKernelItRunsOn(bool android, bool ios, bool macOs, bool windows, bool linux, string? expected)
    {
        NodePlatform.Detect(android, ios, macOs, windows, linux).Should().Be(expected);
    }

    [Theory]
    [InlineData("true", false, true)]
    [InlineData("TRUE", false, true)]
    [InlineData("false", false, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]      // a Docker host leaves /.dockerenv
    public void ALinuxProcessSaysItIsInAContainer_WhenTheImageOrTheHostSaysSo(string? variable, bool dockerEnv, bool expected)
    {
        NodePlatform.DetectContainer(linux: true, name => name == "DOTNET_RUNNING_IN_CONTAINER" ? variable : null, path => dockerEnv && path == "/.dockerenv")
            .Should().Be(expected);
    }

    [Fact]
    public void Podman_LeavesItsOwnFile()
    {
        NodePlatform.DetectContainer(linux: true, _ => null, path => path == "/run/.containerenv").Should().BeTrue();
    }

    [Fact]
    public void AFileOnAnotherSystemIsNoEvidence()
    {
        NodePlatform.DetectContainer(linux: false, _ => null, _ => true).Should().BeFalse("only Linux has such files");
    }

    [Theory]
    [InlineData("windows", "windows")]
    [InlineData("Windows", "windows")]
    [InlineData("  MACOS ", "macos")]
    [InlineData("linux", "linux")]
    [InlineData("android", "android")]
    [InlineData("ios", "ios")]
    [InlineData("plan9", null)]
    [InlineData("<script>alert(1)</script>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WhatAPeerSaysIsOneOfTheKnownNames_OrNothing(string? declared, string? expected)
    {
        NodePlatform.Normalize(declared).Should().Be(expected);
    }

    [Fact]
    public void ThisProcessKnowsWhatItRunsOn()
    {
        NodePlatform.Current.Should().BeOneOf(NodePlatform.Windows, NodePlatform.MacOs, NodePlatform.Linux);
    }

    // ---- what a node remembers of a peer's platform (PeerContactLog) ----

    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void APlatformStaysThroughCalls_Failures_AMovedAddressAndAForgottenOutbound()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NotePlatform(peer, NodePlatform.Linux, inContainer: true);

        log.NoteOutboundOk(peer, T0, "https://a.example");
        log.NoteOutboundFailure(peer, T0.AddMinutes(1), "no answer", "https://a.example");
        log.NoteInbound(peer, T0.AddMinutes(2));
        log.ForgetOutbound(peer);

        var moved = log.Get(peer, "https://b.example");
        moved.Platform.Should().Be(NodePlatform.Linux, "what a node runs on is not a fact of one address");
        moved.InContainer.Should().BeTrue();
    }

    [Fact]
    public void APeerThatSaysNothing_LeavesWhatIsKnown_AndMakesNoEntry()
    {
        var log = new PeerContactLog();
        var known = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        log.NotePlatform(known, NodePlatform.MacOs, inContainer: false);

        log.NotePlatform(known, platform: null, inContainer: null);   // a build before 2.6.4 says nothing
        log.NotePlatform(stranger, platform: null, inContainer: null);

        log.Get(known).Platform.Should().Be(NodePlatform.MacOs);
        log.Get(known).InContainer.Should().BeFalse();
        log.Count.Should().Be(1, "a peer that said nothing is not an entry");
    }

    [Fact]
    public void ANodeThatMoved_ToAnotherPlatform_IsRememberedAsWhatItSaidLast()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NotePlatform(peer, NodePlatform.Windows, inContainer: false);

        log.NotePlatform(peer, NodePlatform.Linux, inContainer: true);

        log.Get(peer).Platform.Should().Be(NodePlatform.Linux);
        log.Get(peer).InContainer.Should().BeTrue();
    }

    [Fact]
    public void ACallThatSaysWhereItCameFrom_IsRemembered_AndALaterOneThatDoesNotLeavesIt()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();

        log.NoteInbound(peer, T0, "203.0.113.7");
        log.NoteInbound(peer, T0.AddMinutes(1));
        log.Get(peer).InboundFrom.Should().Be("203.0.113.7");
        log.Get(peer).InboundAt.Should().Be(T0.AddMinutes(1));

        log.NoteInbound(peer, T0.AddMinutes(2), "198.51.100.2");
        log.Get(peer).InboundFrom.Should().Be("198.51.100.2", "the peer calls from somewhere else now");
    }

    [Fact]
    public void AForgottenPeer_TakesItsPlatformWithIt()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NotePlatform(peer, NodePlatform.Ios, inContainer: false);

        log.Forget(peer);

        log.Get(peer).Platform.Should().BeNull();
    }
}
