using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// Quit routing: the tray menu's Quit and the macOS application menu's Quit (Cmd+Q) both stop the node gracefully, exactly once, before
/// the application shuts down. Faked end to end; what the real window and menu do with it is for a visible session.
/// </summary>
public sealed class QuitCoordinatorTests
{
    private readonly ConcurrentQueue<string> _events = new();
    private readonly List<string> _log = [];

    private QuitCoordinator Make(Action? stop = null, Action? shutdown = null) =>
        new(() => { _events.Enqueue("stop-begin"); stop?.Invoke(); _events.Enqueue("stop-end"); },
            () => { _events.Enqueue("shutdown"); shutdown?.Invoke(); },
            _log.Add);

    [Fact]
    public void Quit_StopsTheNodeFirst_ThenShutsDown()
    {
        var coordinator = Make();

        coordinator.Quit();

        _events.Should().Equal("stop-begin", "stop-end", "shutdown");
        coordinator.NodeStopStarted.Should().BeTrue();
        coordinator.ShutdownRequested.Should().BeTrue();
    }

    [Fact]
    public void QuitTwice_IsHarmless_TheNodeIsStoppedOnce_AndShutdownIsRequestedOnce()
    {
        var coordinator = Make();

        coordinator.Quit();
        coordinator.Quit();
        coordinator.Quit();

        _events.Should().Equal("stop-begin", "stop-end", "shutdown");
    }

    [Fact]
    public void ACmdQ_TheLifetimesOwnShutdownRequest_StopsTheNode_AndDoesNotShutDownItself()
    {
        var coordinator = Make();

        coordinator.OnShutdownRequested();

        _events.Should().Equal("stop-begin", "stop-end");
        coordinator.ShutdownRequested.Should().BeFalse("the lifetime goes on with its own shutdown; the coordinator only makes the node stop first");
    }

    [Fact]
    public void CmdQ_ThenTheTrayQuit_StopsTheNodeOnce()
    {
        var coordinator = Make();

        coordinator.OnShutdownRequested();
        coordinator.Quit();

        _events.Should().Equal("stop-begin", "stop-end", "shutdown");
    }

    [Fact]
    public void TheTrayQuit_ThenTheShutdownRequestItRaisesItself_FindsTheNodeAlreadyStopped()
    {
        QuitCoordinator? coordinator = null;
        coordinator = Make(shutdown: () => coordinator!.OnShutdownRequested());   // desktop.Shutdown() raising the lifetime's request

        coordinator.Quit();

        _events.Should().Equal("stop-begin", "stop-end", "shutdown");
    }

    [Fact]
    public void WhenClosingTheWindowMakesTheLifetimeAskAgain_NothingRecursesAndTheNodeIsStoppedOnce()
    {
        QuitCoordinator? coordinator = null;
        // RealClose() closes the main window; the lifetime reacts with a shutdown request, which asks for the stop again - and a Quit
        coordinator = Make(stop: () => { coordinator!.OnShutdownRequested(); coordinator.Quit(); });

        coordinator.Quit();

        _events.Count(e => e == "stop-begin").Should().Be(1);
        _events.Count(e => e == "shutdown").Should().Be(1);
        _events.First().Should().Be("stop-begin");
    }

    [Fact]
    public void AStopThatFails_DoesNotKeepTheAppOpen_ItIsLogged_AndTheShutdownHappens()
    {
        var coordinator = Make(stop: () => throw new InvalidOperationException("window already closed"));

        var quit = () => coordinator.Quit();

        quit.Should().NotThrow();
        _events.Should().Contain("shutdown");
        _log.Should().ContainSingle().Which.Should().Contain("window already closed");
    }

    [Fact]
    public void AShutdownThatFails_IsLogged_NotThrown()
    {
        var coordinator = Make(shutdown: () => throw new InvalidOperationException("no lifetime"));

        var quit = () => coordinator.Quit();

        quit.Should().NotThrow();
        _log.Should().ContainSingle().Which.Should().Contain("no lifetime");
    }

    [Fact]
    public void FromTwoThreads_TheSecondWaitsForTheStop_ItNeverShutsDownWhileTheNodeStillRuns()
    {
        var stopStarted = new ManualResetEventSlim();
        var coordinator = Make(stop: () =>
        {
            stopStarted.Set();
            Thread.Sleep(300);
        });

        var first = Task.Run(coordinator.OnShutdownRequested);
        stopStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var second = Task.Run(coordinator.Quit);   // arrives while the node is still being stopped
        Task.WaitAll([first, second], TimeSpan.FromSeconds(20)).Should().BeTrue();

        _events.Should().Equal("stop-begin", "stop-end", "shutdown");
    }

    [Fact]
    public void ARequiredArgument_IsChecked()
    {
        var noStop = () => new QuitCoordinator(null!, () => { });
        var noShutdown = () => new QuitCoordinator(() => { }, null!);

        noStop.Should().Throw<ArgumentNullException>();
        noShutdown.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>The menu-bar image: black with alpha, 18 px and 36 px (18 pt @2x), chosen by the platform in one place.</summary>
public sealed class TrayIconAssetTests
{
    private static string AssetsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull();
        return Path.Combine(dir!.FullName, "desktop", "BeeMemoryBank.Desktop", "Assets");
    }

    private static string FileOf(string avaresUri)
    {
        avaresUri.Should().StartWith("avares://BeeMemoryBank.Desktop/Assets/");
        return Path.Combine(AssetsFolder(), avaresUri["avares://BeeMemoryBank.Desktop/Assets/".Length..]);
    }

    [Theory]
    [InlineData(ShellOs.Windows)]
    [InlineData(ShellOs.MacOs)]
    [InlineData(ShellOs.Other)]
    public void TheAssetOfEveryPlatform_IsAFileOfTheApp(ShellOs os)
    {
        File.Exists(FileOf(ShellPlatforms.For(os).TrayIconAsset)).Should().BeTrue();
    }

    [Fact]
    public void TheDesktopProject_PacksTheWholeAssetsFolder_SoTheTemplateImagesAreInTheApp()
    {
        var csproj = File.ReadAllText(Path.Combine(AssetsFolder(), "..", "BeeMemoryBank.Desktop.csproj"));

        csproj.Should().Contain("<AvaloniaResource Include=\"Assets\\**\" />");
    }

    [Theory]
    [InlineData("tray-template.png", 18)]
    [InlineData("tray-template@2x.png", 36)]
    public void TheTemplateImage_IsBlackWithAlpha_AtTheRightSize(string name, int size)
    {
        var png = ReadPng(Path.Combine(AssetsFolder(), name));

        png.Width.Should().Be(size);
        png.Height.Should().Be(size);
        png.Pixels.Length.Should().Be(size * size);
        png.Pixels.Should().OnlyContain(p => p.R == 0 && p.G == 0 && p.B == 0, "a template image is tinted by the system: only its alpha carries the shape");
        png.Pixels.Should().Contain(p => p.A == 0, "there is a transparent background");
        png.Pixels.Should().Contain(p => p.A > 200, "and a solid shape");
        // not a nearly empty or nearly full square
        var covered = png.Pixels.Count(p => p.A > 128) / (double)png.Pixels.Length;
        covered.Should().BeInRange(0.2, 0.8);
    }

    [Fact]
    public void TheWindowsIcon_StaysTheColoredOne()
    {
        ShellPlatforms.For(ShellOs.Windows).TrayIconAsset.Should().EndWith("/Assets/icon.png");
        ShellPlatforms.For(ShellOs.MacOs).TrayIconAsset.Should().Contain("template");
    }

    // ── a small PNG reader (8-bit RGBA, not interlaced), so the test needs no image library ──

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);

    private sealed record Png(int Width, int Height, Pixel[] Pixels);

    private static Png ReadPng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        int width = 0, height = 0;
        var data = new MemoryStream();
        var pos = 8;
        while (pos < bytes.Length)
        {
            var length = (int)ReadUInt32(bytes, pos);
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (type == "IHDR")
            {
                width = (int)ReadUInt32(bytes, pos + 8);
                height = (int)ReadUInt32(bytes, pos + 12);
                bytes[pos + 16].Should().Be(8, "8 bits per channel");
                bytes[pos + 17].Should().Be(6, "RGBA");
                bytes[pos + 20].Should().Be(0, "not interlaced");
            }
            else if (type == "IDAT")
            {
                data.Write(bytes, pos + 8, length);
            }
            pos += 12 + length;
        }

        data.Position = 0;
        using var z = new ZLibStream(data, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var scan = raw.ToArray();
        var stride = width * 4;
        var image = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = scan[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int cur = scan[y * (stride + 1) + 1 + x];
                int left = x >= 4 ? image[y * stride + x - 4] : 0;
                int up = y > 0 ? image[(y - 1) * stride + x] : 0;
                int upLeft = x >= 4 && y > 0 ? image[(y - 1) * stride + x - 4] : 0;
                var value = filter switch
                {
                    0 => cur,
                    1 => cur + left,
                    2 => cur + up,
                    3 => cur + ((left + up) / 2),
                    4 => cur + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("filter " + filter),
                };
                image[y * stride + x] = (byte)value;
            }
        }
        var pixels = new Pixel[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new Pixel(image[i * 4], image[i * 4 + 1], image[i * 4 + 2], image[i * 4 + 3]);
        return new Png(width, height, pixels);
    }

    private static uint ReadUInt32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
