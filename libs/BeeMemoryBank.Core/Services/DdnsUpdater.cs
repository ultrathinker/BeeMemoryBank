using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Service that checks the external IP and updates dynamic DNS if it has changed.
/// </summary>
public class DdnsUpdater
{
    private readonly IExternalIpProvider _ipProvider;
    private readonly IDdnsProvider _ddnsProvider;
    private readonly string _stateFilePath;
    private readonly ILogger<DdnsUpdater>? _logger;

    public DdnsUpdater(
        IExternalIpProvider ipProvider,
        IDdnsProvider ddnsProvider,
        string dataDirectory,
        ILogger<DdnsUpdater>? logger = null)
    {
        _ipProvider = ipProvider ?? throw new ArgumentNullException(nameof(ipProvider));
        _ddnsProvider = ddnsProvider ?? throw new ArgumentNullException(nameof(ddnsProvider));
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Data directory cannot be empty.", nameof(dataDirectory));
        }
        _stateFilePath = Path.Combine(dataDirectory, "ddns-state.json");
        _logger = logger;
    }

    /// <summary>
    /// Checks the current external IP and triggers a DNS update if it differs from the last known IP.
    /// </summary>
    public async Task<DdnsUpdateResult> CheckAndUpdateAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Checking DDNS status...");

        IPAddress? currentIp;
        try
        {
            currentIp = await _ipProvider.GetExternalIpAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error detecting external IP.");
            return DdnsUpdateResult.Failure("Failed to detect external IP.", ex);
        }

        if (currentIp == null)
        {
            // A null IP means detection genuinely failed (e.g. UPnP couldn't reach the router) —
            // this must surface as a failure, not NoChange/IsSuccess=true, or the wizard would show
            // a green "checked — no change" result for a check that didn't actually run.
            _logger?.LogWarning("Detected external IP was null. Skipping update.");
            return DdnsUpdateResult.Failure(
                "Could not detect the external IP (router did not respond to UPnP, or no static IP is configured).");
        }

        var currentIpStr = currentIp.ToString();
        var lastIpStr = await LoadLastKnownIpAsync(cancellationToken);

        if (currentIpStr.Equals(lastIpStr, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("External IP has not changed ({IP}). Skipping update.", currentIpStr);
            return DdnsUpdateResult.NoChange($"IP has not changed from {lastIpStr}.");
        }

        _logger?.LogInformation("External IP changed from '{LastIP}' to '{CurrentIP}'. Updating DNS...", lastIpStr, currentIpStr);

        try
        {
            await _ddnsProvider.UpdateAsync(currentIp, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update DDNS provider.");
            return DdnsUpdateResult.Failure("DNS provider update failed.", ex);
        }

        try
        {
            await SaveLastKnownIpAsync(currentIpStr, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save last-known IP state to disk.");
            // We do not fail the overall operation if only state persistence fails
        }

        return DdnsUpdateResult.Success(currentIpStr, lastIpStr);
    }

    private async Task<string> LoadLastKnownIpAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return string.Empty;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("LastIp", out var ipProp))
            {
                return ipProp.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error reading DDNS state file at {Path}.", _stateFilePath);
        }

        return string.Empty;
    }

    private async Task SaveLastKnownIpAsync(string ip, CancellationToken cancellationToken)
    {
        var state = new DdnsState
        {
            LastIp = ip,
            LastUpdated = DateTimeOffset.UtcNow
        };

        var dir = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_stateFilePath, json, cancellationToken);
    }
}

/// <summary>
/// Serializable DTO for persisting the last-known DDNS state.
/// </summary>
public class DdnsState
{
    public string LastIp { get; set; } = string.Empty;
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>
/// Represents the result of a DDNS check-and-update operation.
/// </summary>
public class DdnsUpdateResult
{
    public bool IsSuccess { get; }
    public bool Changed { get; }
    public string Message { get; }
    public Exception? Exception { get; }

    private DdnsUpdateResult(bool isSuccess, bool changed, string message, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        Changed = changed;
        Message = message;
        Exception = exception;
    }

    public static DdnsUpdateResult Success(string currentIp, string lastIp) =>
        new(true, true, $"Successfully updated IP from {lastIp} to {currentIp}.");

    public static DdnsUpdateResult NoChange(string reason) =>
        new(true, false, reason);

    public static DdnsUpdateResult Failure(string message, Exception? exception = null) =>
        new(false, false, message, exception);
}
