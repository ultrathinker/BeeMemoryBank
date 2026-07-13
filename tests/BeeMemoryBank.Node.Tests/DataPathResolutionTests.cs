using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

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

        var defaultVaultDir = BmbPaths.DefaultVaultDir;
        string? backupDir = null;
        if (Directory.Exists(defaultVaultDir))
        {
            backupDir = defaultVaultDir + "_backup_" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.Move(defaultVaultDir, backupDir);
            }
            catch
            {
                backupDir = null; // If move failed, we won't try to restore
            }
        }

        try
        {
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
            // Restore backup if one was made
            try
            {
                if (Directory.Exists(defaultVaultDir))
                {
                    Directory.Delete(defaultVaultDir, recursive: true);
                }
                if (backupDir != null && Directory.Exists(backupDir))
                {
                    Directory.Move(backupDir, defaultVaultDir);
                }
            }
            catch
            {
                // Ignore restore errors
            }
        }
    }
}
