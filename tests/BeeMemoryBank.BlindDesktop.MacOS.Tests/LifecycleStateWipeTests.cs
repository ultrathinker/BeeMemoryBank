using BeeMemoryBank.BlindDesktop.MacOS;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>The lifecycle removes the host's state after the wipe (the restart report comes after it).</summary>
public class LifecycleStateWipeTests
{
    [Fact]
    public void RestartAfterWipe_RemovesTheStateFileAndItsDamagedCopies_BeforeTheHostIsToldToRestart()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var state = new MacOsBlindStateStore(paths);
        state.Set("bmb.blind.desktop.obsolete_host_state", "x");
        File.WriteAllText(Path.Combine(paths.DataDirectory, "blind-state.json.damaged-1"), "old");
        var keep = Path.Combine(paths.DataDirectory, "keep.txt");
        File.WriteAllText(keep, "x");
        var lifecycle = new MacOsBlindLifecycle(null, state);
        var existedWhenToldToRestart = new List<bool>();
        lifecycle.RestartRequested += () => existedWhenToldToRestart.Add(File.Exists(state.FilePath));

        lifecycle.RestartAfterWipe();

        File.Exists(state.FilePath).Should().BeFalse();
        Directory.GetFiles(paths.DataDirectory, "blind-state.json*").Should().BeEmpty();
        File.Exists(keep).Should().BeTrue();
        existedWhenToldToRestart.Should().Equal(false);
        lifecycle.RestartNeeded.Should().BeTrue();
        lifecycle.WipeWarning.Should().BeNull();
    }

    [Fact]
    public void AStateFileThatCannotBeRemoved_DoesNotStopTheRestart_ButIsReported()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var failing = true;
        var state = new MacOsBlindStateStore(Path.Combine(paths.DataDirectory, "blind-state.json"), null,
            file => { if (failing) throw new UnauthorizedAccessException("denied"); File.Delete(file); });
        state.Set("a", "1");
        var lifecycle = new MacOsBlindLifecycle(null, state);
        var restarts = 0;
        lifecycle.RestartRequested += () => restarts++;

        var act = lifecycle.RestartAfterWipe;

        act.Should().NotThrow();
        restarts.Should().Be(1);
        lifecycle.RestartNeeded.Should().BeTrue();
        lifecycle.WipeWarning.Should().Contain("not removed").And.Contain("blind-state.json");

        failing = false;
        lifecycle.RestartAfterWipe();
        lifecycle.WipeWarning.Should().BeNull("the next wipe removes it");
        File.Exists(state.FilePath).Should().BeFalse();
    }

    [Fact]
    public void WithoutAStateStore_TheWipeStillReportsTheRestart()
    {
        var lifecycle = new MacOsBlindLifecycle();

        lifecycle.RestartAfterWipe();

        lifecycle.RestartNeeded.Should().BeTrue();
        lifecycle.WipeWarning.Should().BeNull();
    }
}
