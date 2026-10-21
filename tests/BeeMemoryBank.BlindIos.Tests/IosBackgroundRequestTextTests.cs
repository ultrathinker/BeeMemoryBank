using BeeMemoryBank.BlindIos.Services;

namespace BeeMemoryBank.BlindIos.Tests;

public sealed class IosBackgroundRequestTextTests
{
    [Fact]
    public void ARefusedBackgroundRequest_UsesOnlyTheBoundedDomainAndCode_NotTheHostileDescription()
    {
        var text = IosBackgroundRequestText.Describe(new("BGTaskSchedulerErrorDomain", 3, "https://node.example/api?token=hunter2 C:\\secret\\bmb_blind_seed_v1"));

        text.Should().Be("iOS refused a background request (BGTaskSchedulerErrorDomain 3).");
        text.Should().NotContain("token=hunter2").And.NotContain("secret").And.NotContain("bmb_blind_");
    }

    [Fact]
    public void ABackgroundRequestDomainOutsideTheBoundedOsForm_IsNotRecorded()
    {
        var text = IosBackgroundRequestText.Describe(new("https://node.example/token=hunter2", -1, "ignored"));

        text.Should().Be("iOS refused a background request (unknown domain -1).");
    }
}
