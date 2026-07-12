using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Verifies <see cref="DdnsUpdater.CheckAndUpdateAsync"/>: the DNS provider must only be invoked
/// when the detected external IP actually differs from the last-known persisted value.
/// </summary>
public class DdnsUpdaterTests : IDisposable
{
    private readonly string _tempDir;

    public DdnsUpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_ddns_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CallsProviderOnlyWhenIpActuallyChanges()
    {
        // Scripted sequence: first a new IP, then the same IP again (no change), then a changed IP.
        var ipProvider = ScriptedIpProvider.Sequence(
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("2.2.2.2"));
        var dns = new RecordingDdnsProvider();
        var updater = new DdnsUpdater(ipProvider, dns, _tempDir);

        var first = await updater.CheckAndUpdateAsync();
        var second = await updater.CheckAndUpdateAsync();
        var third = await updater.CheckAndUpdateAsync();

        // Exactly one provider update per genuine change (2 genuine changes across 3 checks).
        dns.CallCount.Should().Be(2);
        first.Changed.Should().BeTrue();
        second.Changed.Should().BeFalse();
        third.Changed.Should().BeTrue();
        dns.LastIp!.ToString().Should().Be("2.2.2.2");

        // The last-known IP must be persisted to <dataDir>/ddns-state.json.
        var statePath = Path.Combine(_tempDir, "ddns-state.json");
        File.Exists(statePath).Should().BeTrue();
        (await File.ReadAllTextAsync(statePath)).Should().Contain("2.2.2.2");
    }

    [Fact]
    public async Task DoesNotCallProviderWhenIpUnchangedAcrossFreshStart()
    {
        // No prior state file: first check sets the IP, a second identical check must be a no-op.
        var ipProvider = ScriptedIpProvider.Sequence(
            IPAddress.Parse("198.51.100.7"),
            IPAddress.Parse("198.51.100.7"));
        var dns = new RecordingDdnsProvider();
        var updater = new DdnsUpdater(ipProvider, dns, _tempDir);

        var first = await updater.CheckAndUpdateAsync();
        var second = await updater.CheckAndUpdateAsync();

        dns.CallCount.Should().Be(1);
        first.Changed.Should().BeTrue();
        second.Changed.Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsFailureWhenDetectedIpIsNull()
    {
        // A null IP means detection genuinely failed — this must be a Failure (IsSuccess=false),
        // not NoChange, or the wizard would show a green "checked — no change" result for a check
        // that didn't actually run.
        var ipProvider = ScriptedIpProvider.Sequence((IPAddress?)null);
        var dns = new RecordingDdnsProvider();
        var updater = new DdnsUpdater(ipProvider, dns, _tempDir);

        var result = await updater.CheckAndUpdateAsync();

        result.Changed.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        dns.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ReturnsFailureWhenIpProviderThrows()
    {
        var ipProvider = ScriptedIpProvider.Throwing(new InvalidOperationException("router unreachable"));
        var dns = new RecordingDdnsProvider();
        var updater = new DdnsUpdater(ipProvider, dns, _tempDir);

        var result = await updater.CheckAndUpdateAsync();

        result.IsSuccess.Should().BeFalse();
        result.Exception.Should().BeOfType<InvalidOperationException>();
        dns.CallCount.Should().Be(0);
    }
}

// ── Test doubles ───────────────────────────────────────────────────────────────

internal sealed class ScriptedIpProvider : IExternalIpProvider
{
    private readonly Queue<IPAddress?> _ips = new();
    private Exception? _exception;

    private ScriptedIpProvider() { }

    public static ScriptedIpProvider Sequence(params IPAddress?[] ips)
    {
        var p = new ScriptedIpProvider();
        foreach (var ip in ips) p._ips.Enqueue(ip);
        return p;
    }

    public static ScriptedIpProvider Throwing(Exception exception)
    {
        var p = new ScriptedIpProvider { _exception = exception };
        return p;
    }

    public Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default)
    {
        if (_exception != null)
            return Task.FromException<IPAddress?>(_exception);

        return Task.FromResult(_ips.Count > 0 ? _ips.Dequeue() : null);
    }
}

internal sealed class RecordingDdnsProvider : IDdnsProvider
{
    private int _callCount;

    public int CallCount => _callCount;
    public IPAddress? LastIp { get; private set; }

    public Task UpdateAsync(IPAddress ip, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        LastIp = ip;
        return Task.CompletedTask;
    }
}
