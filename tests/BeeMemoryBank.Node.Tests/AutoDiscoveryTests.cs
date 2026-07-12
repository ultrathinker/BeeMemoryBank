using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Node.Tests;

public class AutoDiscoveryTests : IDisposable
{
    private readonly string _tempTestDir;

    public AutoDiscoveryTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "bmb-discovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void Discover_ShouldThrow_IfSiblingDirectoriesMissing()
    {
        // Arrange
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        Directory.CreateDirectory(baseDir);
        var dataDir = Path.Combine(_tempTestDir, "data");

        // Act
        var act = () => AutoDiscovery.Discover(baseDir, dataDir);

        // Assert
        act.Should().Throw<DirectoryNotFoundException>()
           .WithMessage("*Sibling directory*");
    }

    [Fact]
    public void Discover_ShouldThrow_IfNoExecutableOrDllFound()
    {
        // Arrange
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        var apiDir = Path.Combine(_tempTestDir, "api");
        var webDir = Path.Combine(_tempTestDir, "web");
        var dataDir = Path.Combine(_tempTestDir, "data");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(webDir);

        // Act
        var act = () => AutoDiscovery.Discover(baseDir, dataDir);

        // Assert
        act.Should().Throw<FileNotFoundException>()
           .WithMessage("*Could not find executable or DLL*");
    }

    [Fact]
    public void Discover_ShouldResolveExecutables_WhenExeExists()
    {
        // Arrange
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        var apiDir = Path.Combine(_tempTestDir, "api");
        var webDir = Path.Combine(_tempTestDir, "web");
        var dataDir = Path.Combine(_tempTestDir, "data");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(webDir);

        var apiExePath = Path.Combine(apiDir, "BeeMemoryBank.Api.exe");
        var webExePath = Path.Combine(webDir, "BeeMemoryBank.Web.exe");
        File.WriteAllText(apiExePath, "dummy exe content");
        File.WriteAllText(webExePath, "dummy exe content");

        // Act
        var configs = AutoDiscovery.Discover(baseDir, dataDir);

        // Assert
        configs.Should().HaveCount(2);

        var apiConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Api");
        apiConfig.ExecutablePath.Should().Be(apiExePath);
        apiConfig.Arguments.Should().BeNull();
        apiConfig.WorkingDirectory.Should().Be(apiDir);

        var webConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Web");
        webConfig.ExecutablePath.Should().Be(webExePath);
        webConfig.Arguments.Should().BeNull();
        webConfig.WorkingDirectory.Should().Be(webDir);
    }

    [Fact]
    public void Discover_ShouldResolveDll_WhenOnlyDllExists()
    {
        // Arrange
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        var apiDir = Path.Combine(_tempTestDir, "api");
        var webDir = Path.Combine(_tempTestDir, "web");
        var dataDir = Path.Combine(_tempTestDir, "data");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(webDir);

        var apiDllPath = Path.Combine(apiDir, "BeeMemoryBank.Api.dll");
        var webDllPath = Path.Combine(webDir, "BeeMemoryBank.Web.dll");
        File.WriteAllText(apiDllPath, "dummy dll content");
        File.WriteAllText(webDllPath, "dummy dll content");

        // Act
        var configs = AutoDiscovery.Discover(baseDir, dataDir);

        // Assert
        configs.Should().HaveCount(2);

        var apiConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Api");
        apiConfig.ExecutablePath.Should().Be("dotnet");
        apiConfig.Arguments.Should().Be($"\"{apiDllPath}\"");
        apiConfig.WorkingDirectory.Should().Be(apiDir);

        var webConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Web");
        webConfig.ExecutablePath.Should().Be("dotnet");
        webConfig.Arguments.Should().Be($"\"{webDllPath}\"");
        webConfig.WorkingDirectory.Should().Be(webDir);
    }

    [Fact]
    public void Discover_ShouldPreferExe_WhenBothExeAndDllExist()
    {
        // Arrange
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        var apiDir = Path.Combine(_tempTestDir, "api");
        var webDir = Path.Combine(_tempTestDir, "web");
        var dataDir = Path.Combine(_tempTestDir, "data");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(webDir);

        var apiExePath = Path.Combine(apiDir, "BeeMemoryBank.Api.exe");
        var apiDllPath = Path.Combine(apiDir, "BeeMemoryBank.Api.dll");
        File.WriteAllText(apiExePath, "dummy exe");
        File.WriteAllText(apiDllPath, "dummy dll");

        var webExePath = Path.Combine(webDir, "BeeMemoryBank.Web.exe");
        var webDllPath = Path.Combine(webDir, "BeeMemoryBank.Web.dll");
        File.WriteAllText(webExePath, "dummy exe");
        File.WriteAllText(webDllPath, "dummy dll");

        // Act
        var configs = AutoDiscovery.Discover(baseDir, dataDir);

        // Assert
        var apiConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Api");
        apiConfig.ExecutablePath.Should().Be(apiExePath);
        apiConfig.Arguments.Should().BeNull();

        var webConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Web");
        webConfig.ExecutablePath.Should().Be(webExePath);
        webConfig.Arguments.Should().BeNull();
    }

    [Fact]
    public void Discover_ShouldBuildExpectedEnvironmentVariables()
    {
        // Arrange
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        var apiDir = Path.Combine(_tempTestDir, "api");
        var webDir = Path.Combine(_tempTestDir, "web");
        var dataDir = Path.Combine(_tempTestDir, "data");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(webDir);

        var apiExePath = Path.Combine(apiDir, "BeeMemoryBank.Api.exe");
        var webExePath = Path.Combine(webDir, "BeeMemoryBank.Web.exe");
        File.WriteAllText(apiExePath, "dummy");
        File.WriteAllText(webExePath, "dummy");

        // Act
        var configs = AutoDiscovery.Discover(baseDir, dataDir);

        // Assert
        var apiConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Api");
        var apiEnv = apiConfig.EnvironmentVariables;
        apiEnv.Should().NotBeNull();
        apiEnv!["ASPNETCORE_URLS"].Should().Be("http://127.0.0.1:0");
        apiEnv["BMB_READY_FILE"].Should().Be(Path.Combine(dataDir, "api.ready"));
        apiEnv["BMB_STDIN_LIFELINE"].Should().Be("1");
        apiEnv["BMB_BEHIND_LOOPBACK_PROXY"].Should().Be("1");
        apiEnv["BMB_DATA_PATH"].Should().Be(dataDir);
    }

    [Fact]
    public void Discover_ShouldNotSetApiUrl_ForWebEnvironmentVariables()
    {
        // BMB_API_URL isn't set for Web in auto-discovered mode - Api's port isn't known
        // until its ready-file appears, and NodeOrchestrator starts children concurrently
        // with no staged "wait for Api, then start Web" support yet. Web already falls back
        // to a sensible default when this env var is absent (see its own Program.cs).
        var baseDir = Path.Combine(_tempTestDir, "bmbd");
        var apiDir = Path.Combine(_tempTestDir, "api");
        var webDir = Path.Combine(_tempTestDir, "web");
        var dataDir = Path.Combine(_tempTestDir, "data");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(webDir);

        File.WriteAllText(Path.Combine(apiDir, "BeeMemoryBank.Api.exe"), "dummy");
        File.WriteAllText(Path.Combine(webDir, "BeeMemoryBank.Web.exe"), "dummy");

        var configs = AutoDiscovery.Discover(baseDir, dataDir);

        var webConfig = configs.First(c => c.ApplicationName == "BeeMemoryBank.Web");
        webConfig.EnvironmentVariables.Should().NotBeNull();
        webConfig.EnvironmentVariables!.Should().NotContainKey("BMB_API_URL");
    }
}
