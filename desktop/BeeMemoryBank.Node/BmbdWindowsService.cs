using System;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Node;

[SupportedOSPlatform("windows")]
internal sealed class BmbdWindowsService : ServiceBase
{
    private readonly bool _isAutoMode;
    private readonly string? _dataDirectory;
    private readonly string? _configPath;
    private CancellationTokenSource? _cts;
    private Task<int>? _runTask;

    public BmbdWindowsService(bool isAutoMode, string? dataDirectory, string? configPath)
    {
        _isAutoMode = isAutoMode;
        _dataDirectory = dataDirectory;
        _configPath = configPath;
        ServiceName = "bmbd";
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _runTask = Program.RunOrchestratorAsync(_isAutoMode, _dataDirectory, _configPath, _cts.Token);

        // RunOrchestratorAsync is only expected to complete when OnStop cancels it. If it
        // completes on its own (config error, critical child failure, unhandled exception),
        // SCM must be told the service actually stopped — otherwise it stays "Running" until
        // someone notices and stops it manually, and any configured failure-recovery action
        // (auto-restart) never triggers.
        _runTask.ContinueWith(t =>
        {
            if (_cts is not { IsCancellationRequested: false }) return; // OnStop already handling shutdown
            ExitCode = t.IsFaulted || t.IsCanceled ? 1 : t.Result;
            Stop();
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(15));
        }
        catch
        {
            // Ignore wait exceptions (e.g. OperationCanceledException) on shutdown
        }
        finally
        {
            _cts?.Dispose();
        }
    }
}
