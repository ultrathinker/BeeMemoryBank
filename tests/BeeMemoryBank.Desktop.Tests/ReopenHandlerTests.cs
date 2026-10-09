using System;
using Avalonia.Controls.ApplicationLifetimes;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>A click on the Dock icon of the running Mac app (the system's reopen request) shows the window.</summary>
public sealed class ReopenHandlerTests
{
    [Fact]
    public void AReopenRequest_ShowsTheWindow()
    {
        var shown = 0;

        ReopenHandler.Handle(ActivationKind.Reopen, () => shown++);

        shown.Should().Be(1);
    }

    [Fact]
    public void EveryOtherKindOfActivation_DoesNotPopTheWindowUp()
    {
        foreach (var kind in Enum.GetValues<ActivationKind>())
        {
            if (kind == ActivationKind.Reopen) continue;
            var shown = 0;

            ReopenHandler.Handle(kind, () => shown++);

            shown.Should().Be(0, $"{kind}: the app going to the background or opening a file is not a request to open the window");
        }
    }
}
