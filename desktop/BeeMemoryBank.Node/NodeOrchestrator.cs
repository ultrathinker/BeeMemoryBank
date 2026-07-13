using System.Diagnostics;
using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Node;

/// <summary>
/// Orchestrator component that manages the lifecycle of multiple child processes,
/// waits for their ready-file notifications, monitors their health, performs backoff restarts,
/// and maintains data directory locking and status reports.
/// </summary>
public class NodeOrchestrator : IDisposable
{
    private readonly string _dataDirectory;
    private readonly IReadOnlyList<ChildProcessConfig> _configs;
    private readonly List<MonitoredChild> _children = new();
    private readonly NodeStatusManager _statusManager;
    private readonly Func<int, TimeSpan> _backoffPolicy;
    private readonly object _lock = new();

    private DirectoryLock? _directoryLock;
    private CancellationTokenSource? _lifecycleCts;
    private readonly List<Task> _childTasks = new();
    private bool _isStopping;
    private bool _hasFailed;
    private WindowsJobObject? _jobObject;

    public event Action? OnAllReady;
    public event Action<string>? OnCriticalFailure;

    public bool AllReady { get; private set; }
    public bool HasFailed => _hasFailed;

    public IReadOnlyDictionary<string, ReadyFileInfo> ReadyChildren
    {
        get
        {
            lock (_lock)
            {
                return _children
                    .Where(c => c.ReadyInfo != null)
                    .ToDictionary(c => c.Config.ApplicationName, c => c.ReadyInfo!);
            }
        }
    }

    public NodeOrchestrator(string dataDirectory, IReadOnlyList<ChildProcessConfig> configs)
        : this(dataDirectory, configs, null)
    {
    }

    public NodeOrchestrator(
        string dataDirectory, 
        IReadOnlyList<ChildProcessConfig> configs, 
        Func<int, TimeSpan>? backoffPolicy)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _statusManager = new NodeStatusManager(dataDirectory);
        _backoffPolicy = backoffPolicy ?? (attempt => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 16)));
    }

    /// <summary>
    /// Starts the directory locking, starts children, and waits for them to become ready.
    /// Throws InvalidOperationException if the lock cannot be acquired or if startup fails.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_lifecycleCts != null)
            {
                throw new InvalidOperationException("Orchestrator has already been started.");
            }
            _lifecycleCts = new CancellationTokenSource();
            _isStopping = false;
            _hasFailed = false;
            AllReady = false;

            if (OperatingSystem.IsWindows())
            {
                _jobObject = new WindowsJobObject();
            }
        }

        try
        {
            // Acquire directory lock (holds an exclusive file lock on node.lock)
            _directoryLock = DirectoryLock.Acquire(_dataDirectory);
        }
        catch
        {
            // Setup failed before orchestration ever started; roll back so a later
            // retry isn't permanently blocked by the "already started" guard above.
            lock (_lock)
            {
                _lifecycleCts.Dispose();
                _lifecycleCts = null;
            }
            throw;
        }

        lock (_lock)
        {
            // Prepare monitored children
            foreach (var config in _configs)
            {
                _children.Add(new MonitoredChild(config));
            }
        }

        // Start lifecycle tasks
        foreach (var child in _children)
        {
            var task = RunChildLifecycleObservedAsync(child, _lifecycleCts.Token);
            lock (_lock)
            {
                _childTasks.Add(task);
            }
        }

        // With zero children, no per-child lifecycle loop will ever run to report readiness,
        // so the "all ready" check must be triggered here instead or it would never resolve.
        CheckAndPublishOverallStatus();

        // Wait until all children are ready, or orchestration fails/cancels
        await WaitForAllReadyOrFailureAsync(cancellationToken);
    }

    public async Task WaitForAllReadyOrFailureAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (_hasFailed)
                {
                    throw new InvalidOperationException("Orchestrator failed to start one or more child processes.");
                }
                if (AllReady)
                {
                    return;
                }
                // StopAsync() unconditionally sets AllReady = false as part of shutting down,
                // and nothing ever sets it back to true once _isStopping is set - if Stop was
                // triggered concurrently (e.g. stdin EOF arriving essentially simultaneously
                // with readiness, before this loop's next poll observes AllReady briefly being
                // true), this loop would otherwise spin forever waiting for a condition that can
                // structurally never become true again, since `cancellationToken` here is
                // frequently CancellationToken.None (never itself cancelled) and _isStopping
                // doesn't set _hasFailed. Observed live: the whole process hanging indefinitely
                // after StopAsync had already logged "Stopped successfully".
                if (_isStopping)
                {
                    throw new OperationCanceledException("Orchestrator is stopping.");
                }
            }
            await Task.Delay(100, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void StartAdditionalChild(ChildProcessConfig config)
    {
        MonitoredChild child;
        CancellationToken token;

        lock (_lock)
        {
            if (_lifecycleCts == null || _lifecycleCts.IsCancellationRequested)
            {
                throw new InvalidOperationException("Orchestrator is not running.");
            }

            child = new MonitoredChild(config);
            _children.Add(child);
            AllReady = false;

            // Delete the status file since not all children are ready now
            _statusManager.DeleteStatus();
            
            token = _lifecycleCts.Token;
        }

        var task = RunChildLifecycleObservedAsync(child, token);

        lock (_lock)
        {
            _childTasks.Add(task);
        }
    }

    /// <summary>
    /// Wraps <see cref="RunChildLifecycleAsync"/> so an unexpected exception (anything other than
    /// cancellation) is reported via <see cref="TriggerCriticalFailure"/> instead of silently
    /// vanishing. Previously this was handled centrally by a single <c>Task.WhenAll</c> wrapper
    /// around every child's task; that wrapper was removed when children started being added
    /// individually (StartAdditionalChild), which silently dropped this fault-handling — a fault
    /// before readiness would leave <see cref="_hasFailed"/> false forever and
    /// <see cref="WaitForAllReadyOrFailureAsync"/> would poll indefinitely.
    /// </summary>
    private async Task RunChildLifecycleObservedAsync(MonitoredChild child, CancellationToken stoppingToken)
    {
        try
        {
            await RunChildLifecycleAsync(child, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown or abort.
        }
        catch (Exception ex)
        {
            TriggerCriticalFailure($"Child '{child.Config.ApplicationName}' lifecycle task failed unexpectedly: {ex.Message}");
        }
    }

    private async Task RunChildLifecycleAsync(MonitoredChild child, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Delete existing ready file to avoid reading stale data from previous run
            DeleteReadyFileSilently(child.Config.ReadyFilePath);

            Console.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Starting: {child.Config.ExecutablePath} {child.Config.Arguments}");
            var psi = new ProcessStartInfo
            {
                FileName = child.Config.ExecutablePath,
                Arguments = child.Config.Arguments ?? string.Empty,
                WorkingDirectory = child.Config.WorkingDirectory,
                RedirectStandardInput = true, // Redirect stdin to enable signaling clean shutdown by closing it
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (child.Config.EnvironmentVariables != null)
            {
                foreach (var (key, val) in child.Config.EnvironmentVariables)
                {
                    psi.EnvironmentVariables[key] = val;
                }
            }

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
                if (OperatingSystem.IsWindows() && _jobObject != null)
                {
                    _jobObject.AssignProcess(process);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Failed to start: {ex.Message}");
                await HandleChildFailureAsync(child, stoppingToken);
                continue;
            }

            child.CurrentProcess = process;
            child.ReadyInfo = null;

            // Wait for either the ready-file or the process exiting
            var readyTask = WaitForReadyFileAsync(child, stoppingToken);
            var exitTask = WaitForExitAsync(process, stoppingToken);

            var completedTask = await Task.WhenAny(readyTask, exitTask);

            if (completedTask == readyTask && readyTask.Result != null)
            {
                child.ReadyInfo = readyTask.Result;
                Console.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Process ready (PID {child.ReadyInfo.Pid}). URLs: {string.Join(", ", child.ReadyInfo.Urls)}");
                
                CheckAndPublishOverallStatus();
                ScheduleFailureReset(child);

                // Now wait for exit
                await exitTask;
            }

            child.CancelFailureReset();

            // Clean up process resources
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }

            child.CurrentProcess = null;
            child.ReadyInfo = null;

            // Check if we are stopping or if the process crashed unexpectedly
            bool stopping;
            lock (_lock)
            {
                stopping = _isStopping;
                if (AllReady)
                {
                    AllReady = false;
                    _statusManager.DeleteStatus();
                }
            }

            if (stopping || stoppingToken.IsCancellationRequested)
            {
                break;
            }

            Console.Error.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Process exited unexpectedly.");
            await HandleChildFailureAsync(child, stoppingToken);
        }
    }

    private async Task<ReadyFileInfo?> WaitForReadyFileAsync(MonitoredChild child, CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout && !stoppingToken.IsCancellationRequested)
        {
            if (child.CurrentProcess?.HasExited == true)
            {
                return null;
            }

            var result = ReadyFileManager.Read(child.Config.ReadyFilePath);
            if (result.Success && result.Info != null)
            {
                return result.Info;
            }

            try
            {
                await Task.Delay(200, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!stoppingToken.IsCancellationRequested && child.CurrentProcess?.HasExited == false)
        {
            Console.Error.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Ready file wait timed out.");
        }
        return null;
    }

    private async Task WaitForExitAsync(Process process, CancellationToken stoppingToken)
    {
        try
        {
            await process.WaitForExitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Ignored on cancellation
        }
    }

    private async Task HandleChildFailureAsync(MonitoredChild child, CancellationToken stoppingToken)
    {
        child.ConsecutiveFailures++;
        if (child.ConsecutiveFailures > 5)
        {
            var msg = $"[{child.Config.ApplicationName}] Failed 5 consecutive times to start or stay running. Transitioning to Failed state.";
            Console.Error.WriteLine($"[Orchestrator] {msg}");
            TriggerCriticalFailure(msg);
            throw new OperationCanceledException(msg);
        }

        var delay = _backoffPolicy(child.ConsecutiveFailures);

        Console.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Restarting in {delay.TotalSeconds}s (attempt {child.ConsecutiveFailures}/5).");
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Suppress
        }
    }

    private void ScheduleFailureReset(MonitoredChild child)
    {
        child.CancelFailureReset();
        child.SuccessResetCts = new CancellationTokenSource();
        var token = child.SuccessResetCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                if (!token.IsCancellationRequested)
                {
                    child.ConsecutiveFailures = 0;
                    Console.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Running stably for 30s, reset consecutive failure count.");
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void CheckAndPublishOverallStatus()
    {
        lock (_lock)
        {
            if (_isStopping || _hasFailed || AllReady) return;

            if (_children.All(c => c.ReadyInfo != null))
            {
                AllReady = true;
                var dict = _children.ToDictionary(c => c.Config.ApplicationName, c => c.ReadyInfo!);
                _statusManager.WriteStatus(dict);
                Console.WriteLine("[Orchestrator] All processes are READY. Status file written.");
                OnAllReady?.Invoke();
            }
        }
    }

    private void TriggerCriticalFailure(string reason)
    {
        lock (_lock)
        {
            if (_hasFailed) return;
            _hasFailed = true;
        }

        try
        {
            OnCriticalFailure?.Invoke(reason);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Orchestrator] Error invoking OnCriticalFailure: {ex.Message}");
        }

        // Stop other processes asynchronously
        Task.Run(() => StopAsync());
    }

    /// <summary>
    /// Updates the front URL in the status and runtime files by re-writing them.
    /// </summary>
    public void UpdateFrontUrl(string frontUrl)
    {
        lock (_lock)
        {
            var tempManager = new NodeStatusManager(_dataDirectory, frontUrl);
            var readyDict = _children
                .Where(c => c.ReadyInfo != null)
                .ToDictionary(c => c.Config.ApplicationName, c => c.ReadyInfo!);
            tempManager.WriteStatus(readyDict);
        }
    }

    /// <summary>
    /// Clean shutdown: signals all child processes to stop by closing their stdin,
    /// waits for them to stop, kills them if they timeout, and cleans status/locks.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_isStopping) return;
            _isStopping = true;
            AllReady = false;
            cts = _lifecycleCts;
        }

        Console.WriteLine("[Orchestrator] Stopping all managed child processes...");

        if (cts != null)
        {
            cts.Cancel();
        }

        List<MonitoredChild> childrenSnapshot;
        lock (_lock)
        {
            childrenSnapshot = _children.ToList();
        }

        var stopTasks = childrenSnapshot.Select(async child =>
        {
            var proc = child.CurrentProcess;
            if (proc == null || proc.HasExited) return;

            Console.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Closing standard input...");
            try
            {
                proc.StandardInput.Close();
            }
            catch { }

            var waitTask = proc.WaitForExitAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));

            if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[Orchestrator] [{child.Config.ApplicationName}] Did not exit within 5s. Killing process tree...");
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch { }
            }

            try
            {
                proc.Dispose();
            }
            catch { }
        }).ToList();

        await Task.WhenAll(stopTasks);

        List<Task> childTasksCopy;
        lock (_lock)
        {
            childTasksCopy = _childTasks.ToList();
        }
        try
        {
            await Task.WhenAll(childTasksCopy);
        }
        catch { }

        // Clean files
        _statusManager.DeleteStatus();

        if (_directoryLock != null)
        {
            _directoryLock.Dispose();
            _directoryLock = null;
        }

        if (_jobObject != null)
        {
            _jobObject.Dispose();
            _jobObject = null;
        }

        Console.WriteLine("[Orchestrator] Stopped successfully.");
    }

    private static void DeleteReadyFileSilently(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _lifecycleCts?.Dispose();
        if (_jobObject != null)
        {
            _jobObject.Dispose();
            _jobObject = null;
        }
    }

    private class MonitoredChild
    {
        public ChildProcessConfig Config { get; }
        public Process? CurrentProcess { get; set; }
        public int ConsecutiveFailures { get; set; }
        public ReadyFileInfo? ReadyInfo { get; set; }
        public CancellationTokenSource? SuccessResetCts { get; set; }

        public MonitoredChild(ChildProcessConfig config)
        {
            Config = config;
        }

        public void CancelFailureReset()
        {
            if (SuccessResetCts != null)
            {
                try
                {
                    SuccessResetCts.Cancel();
                }
                catch { }
                SuccessResetCts.Dispose();
                SuccessResetCts = null;
            }
        }
    }
}
