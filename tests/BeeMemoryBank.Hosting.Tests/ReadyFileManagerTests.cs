using System;
using System.IO;
using System.Threading.Tasks;
using BeeMemoryBank.Hosting;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Hosting.Tests;

public class ReadyFileManagerTests : IDisposable
{
    private readonly string _tempDirectory;

    public ReadyFileManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ReadyFileManagerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void WriteAndRead_Sync_RoundTrip_WorksCorrectly()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "ready.json");
        var originalInfo = new ReadyFileInfo(
            Pid: 1234,
            Urls: new[] { "http://127.0.0.1:5000", "https://127.0.0.1:5001" },
            ApplicationName: "TestApp",
            Version: "2.1.0",
            StartupTimeUtc: new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc)
        );

        // Act
        ReadyFileManager.Write(filePath, originalInfo);
        var result = ReadyFileManager.Read(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(ReadyFileReadStatus.Success);
        result.ErrorMessage.Should().BeNull();
        result.Info.Should().NotBeNull();
        result.Info!.Pid.Should().Be(originalInfo.Pid);
        result.Info.Urls.Should().BeEquivalentTo(originalInfo.Urls);
        result.Info.ApplicationName.Should().Be(originalInfo.ApplicationName);
        result.Info.Version.Should().Be(originalInfo.Version);
        result.Info.StartupTimeUtc.Should().Be(originalInfo.StartupTimeUtc);
    }

    [Fact]
    public async Task WriteAndRead_Async_RoundTrip_WorksCorrectly()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "ready_async.json");
        var originalInfo = new ReadyFileInfo(
            Pid: 5678,
            Urls: new[] { "http://127.0.0.1:8080" },
            ApplicationName: "TestAppAsync",
            Version: "1.0.0-beta",
            StartupTimeUtc: new DateTime(2026, 7, 12, 12, 30, 0, DateTimeKind.Utc)
        );

        // Act
        await ReadyFileManager.WriteAsync(filePath, originalInfo);
        var result = await ReadyFileManager.ReadAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(ReadyFileReadStatus.Success);
        result.ErrorMessage.Should().BeNull();
        result.Info.Should().NotBeNull();
        result.Info!.Pid.Should().Be(originalInfo.Pid);
        result.Info.Urls.Should().BeEquivalentTo(originalInfo.Urls);
        result.Info.ApplicationName.Should().Be(originalInfo.ApplicationName);
        result.Info.Version.Should().Be(originalInfo.Version);
        result.Info.StartupTimeUtc.Should().Be(originalInfo.StartupTimeUtc);
    }

    [Fact]
    public void Read_NonExistentFile_ReturnsFileNotFound()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "doesnotexist.json");

        // Act
        var result = ReadyFileManager.Read(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(ReadyFileReadStatus.FileNotFound);
        result.Info.Should().BeNull();
        result.ErrorMessage.Should().Contain("exist");
    }

    [Fact]
    public async Task ReadAsync_NonExistentFile_ReturnsFileNotFound()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "doesnotexist_async.json");

        // Act
        var result = await ReadyFileManager.ReadAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(ReadyFileReadStatus.FileNotFound);
        result.Info.Should().BeNull();
        result.ErrorMessage.Should().Contain("exist");
    }

    [Theory]
    [InlineData("{ invalid json }")]
    [InlineData("{\"pid\":123}")] // missing required properties
    [InlineData("null")]
    [InlineData("")]
    public void Read_CorruptedOrIncompleteJson_ReturnsCorruptedJson(string invalidContent)
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "corrupted.json");
        File.WriteAllText(filePath, invalidContent);

        // Act
        var result = ReadyFileManager.Read(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(ReadyFileReadStatus.CorruptedJson);
        result.Info.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Write_ShouldNotLeaveTempFiles()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "atomic.json");
        var info = new ReadyFileInfo(111, new[] { "http://localhost" }, "App", "1.0", DateTime.UtcNow);

        // Act
        ReadyFileManager.Write(filePath, info);

        // Assert
        File.Exists(filePath).Should().BeTrue();
        
        // Ensure no temp files starting with target filename or ending in .tmp exist in directory
        var tempFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        tempFiles.Should().BeEmpty();
    }

    [Fact]
    public void Write_ShouldOverwriteExistingFileAtomically()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "overwrite.json");
        var firstInfo = new ReadyFileInfo(1, new[] { "http://localhost:1" }, "App", "1.0", DateTime.UtcNow);
        var secondInfo = new ReadyFileInfo(2, new[] { "http://localhost:2" }, "App", "2.0", DateTime.UtcNow);

        // Act
        ReadyFileManager.Write(filePath, firstInfo);
        ReadyFileManager.Write(filePath, secondInfo);
        var result = ReadyFileManager.Read(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.Info!.Pid.Should().Be(2);
        
        var tempFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        tempFiles.Should().BeEmpty();
    }
}
