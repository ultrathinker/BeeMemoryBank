using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

// See NodeProcessEnvCollection (EndToEndIntegrationTests.cs) — this class mutates the process-wide
// BMB_DATA_PATH environment variable, which must not run concurrently with anything that spawns a
// real BeeMemoryBank.Node.exe subprocess.
[Collection("NodeProcessEnv")]
public class DataPathResolutionTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _dirsToDelete = new();

    public void Dispose()
    {
        // Clean up environment variable
        Environment.SetEnvironmentVariable("BMB_DATA_PATH", null);

        // Clean up created directories
        foreach (var dir in _dirsToDelete)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private string GetTempPath(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        _dirsToDelete.Add(path);
        return path;
    }

    [Fact]
    public async Task RunOrchestratorAsync_ExplicitDataArg_ShouldWinOverEnvAndDefault()
    {
        // Arrange
        var explicitPath = GetTempPath("bmb-explicit");
        var envPath = GetTempPath("bmb-env");

        Environment.SetEnvironmentVariable("BMB_DATA_PATH", envPath);

        // Act
        // Auto-discovery will fail because api/web siblings don't exist under AppContext.BaseDirectory,
        // which returns 1. But the directory should still be resolved and created first.
        var exitCode = await Program.RunOrchestratorAsync(
            isAutoMode: true,
            dataDirectory: explicitPath,
            configPath: null,
            stopToken: CancellationToken.None);

        // Assert
        exitCode.Should().Be(1); // auto-discovery failed exit code
        Directory.Exists(explicitPath).Should().BeTrue();
        Directory.Exists(envPath).Should().BeFalse();
    }

    [Fact]
    public async Task RunOrchestratorAsync_EnvVar_ShouldWinOverDefault_WhenNoExplicitArg()
    {
        // Arrange
        var envPath = GetTempPath("bmb-env");
        Environment.SetEnvironmentVariable("BMB_DATA_PATH", envPath);

        // Act
        var exitCode = await Program.RunOrchestratorAsync(
            isAutoMode: true,
            dataDirectory: null,
            configPath: null,
            stopToken: CancellationToken.None);

        // Assert
        exitCode.Should().Be(1);
        Directory.Exists(envPath).Should().BeTrue();
    }

    [Fact]
    public async Task RunOrchestratorAsync_DefaultVaultDir_ShouldBeUsed_WhenNoExplicitArgAndNoEnvVar()
    {
        // Arrange
        Environment.SetEnvironmentVariable("BMB_DATA_PATH", null);

        // Sandbox BmbPaths.Root via a LOCALAPPDATA override instead of moving the real
        // default vault dir aside: on a machine with a real BeeMemoryBank install, a failed
        // Directory.Move (e.g. a sharing violation from the real app holding node.lock) used
        // to leave backupDir null while the test still ran against -- and the finally block
        // still unconditionally deleted -- the real, un-backed-up vault directory. LOCALAPPDATA
        // is honored by BmbPaths.Root (see libs/BeeMemoryBank.AppPaths/BmbPaths.cs), so this
        // is now genuinely isolated rather than relying on a move-and-restore dance.
        var fakeLocalAppData = GetTempPath("bmb-localappdata");
        Directory.CreateDirectory(fakeLocalAppData);
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

        try
        {
            var defaultVaultDir = BmbPaths.DefaultVaultDir;

            // Act
            var exitCode = await Program.RunOrchestratorAsync(
                isAutoMode: true,
                dataDirectory: null,
                configPath: null,
                stopToken: CancellationToken.None);

            // Assert
            exitCode.Should().Be(1);
            Directory.Exists(defaultVaultDir).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }
    }
}
