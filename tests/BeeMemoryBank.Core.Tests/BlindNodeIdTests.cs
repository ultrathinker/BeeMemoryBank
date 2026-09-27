using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Tests;

/// <summary>The blind-node mark in a NodeId (BMB-43, plan section 3.1).</summary>
public class BlindNodeIdTests
{
    [Fact]
    public void A_new_blind_id_carries_the_mark_and_is_version_8()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = BlindNodeId.NewId();
            BlindNodeId.IsBlind(id).Should().BeTrue();
            id.Version.Should().Be(8);
            id.ToString("D").Should().StartWith("b11d");
        }
    }

    [Fact]
    public void Ordinary_ids_are_never_blind()
    {
        for (var i = 0; i < 1000; i++)
        {
            BlindNodeId.IsBlind(Guid.NewGuid()).Should().BeFalse("v4 ids never have version 8");
            BlindNodeId.IsBlind(Guid.CreateVersion7()).Should().BeFalse("v7 ids never have version 8");
        }
    }

    [Fact]
    public void The_check_ignores_case_and_reads_strings()
    {
        var id = BlindNodeId.NewId();
        BlindNodeId.IsBlind(id.ToString("D").ToUpperInvariant()).Should().BeTrue("the database keeps NodeIds uppercase");
        BlindNodeId.IsBlind(id.ToString("D")).Should().BeTrue();
        BlindNodeId.IsBlind((string?)null).Should().BeFalse();
        BlindNodeId.IsBlind("not-a-guid").Should().BeFalse();
    }

    [Fact]
    public void A_v8_id_without_the_prefix_is_not_blind()
    {
        var other = Guid.ParseExact("0000abcd-1234-8123-9123-0123456789ab", "D");
        other.Version.Should().Be(8);
        BlindNodeId.IsBlind(other).Should().BeFalse();
    }

    [Theory]
    [InlineData("blind", true)]
    [InlineData(" BLIND ", true)]
    [InlineData("full", false)]
    [InlineData(null, false)]
    public void The_role_comes_from_BMB_ROLE(string? value, bool blind) =>
        new EnvironmentNodeRole(value).IsBlind.Should().Be(blind);
}
