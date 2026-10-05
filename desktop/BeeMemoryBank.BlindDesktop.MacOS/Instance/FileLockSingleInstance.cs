using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>
/// One blind app per data folder on Unix, where .NET has no named mutex or named event. The first instance takes an exclusive lock on a
/// file in the app's own folder (a <see cref="FileStream"/> with <see cref="FileShare.None"/>, which .NET implements with <c>flock</c>;
/// the lock goes away with the process, however it ends) and listens on a Unix domain socket next to it. A second start finds the lock
/// taken, connects to the socket, sends one word ("show") and ends; the first instance then shows its window.
///
/// <para>Nothing listens on a network port: the socket is a file in the app's private folder (mode 0700; the socket file itself 0600), so
/// only the same user can talk to it. It accepts one thing, the word "show", and reads at most a few bytes within one second, so a
/// stray connection can neither hang nor feed it anything. A socket path longer than the system allows (about 100 bytes; a long scratch
/// folder) moves to the user's private temp folder under a short name derived from the folder; if even that does not fit, there is no
/// activation signal (the lock still keeps the second copy out).</para>
///
/// <para>The lock file is never removed (removing it would let two processes lock two different files). The socket file is, on
/// <see cref="Dispose"/>; a stale one after a crash is replaced at the next start, which is safe because the lock proves that nobody
/// else is listening.</para>
/// </summary>
public sealed class FileLockSingleInstance : IDisposable
{
    public const string LockFileName = ".instance.lock";
    public const string SocketFileName = ".instance.sock";
    public const string ShowCommand = "show\n";

    /// <summary>sockaddr_un.sun_path is 104 bytes on macOS (108 on Linux), terminator included: stay well inside.</summary>
    private const int MaxSocketPathBytes = 100;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);

    private readonly FileStream _lock;
    private readonly string? _socketPath;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private Task? _acceptLoop;
    private Action? _onActivate;
    private Action<Exception>? _onError;
    private bool _pending;
    private bool _disposed;

    private FileLockSingleInstance(FileStream lockStream, string? socketPath)
    {
        _lock = lockStream;
        _socketPath = socketPath;
    }

    /// <summary>If the instance could not start listening (the lock is held anyway), why; otherwise null.</summary>
    public string? ActivationUnavailable { get; private set; }

    /// <summary>Where the activation socket is (or would be) for a data folder; null when no short enough path exists.</summary>
    public static string? SocketPathFor(string dataDirectory)
    {
        var direct = Path.Combine(Path.GetFullPath(dataDirectory), SocketFileName);
        if (Encoding.UTF8.GetByteCount(direct) <= MaxSocketPathBytes) return direct;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))))[..16].ToLowerInvariant();
        var inTemp = Path.Combine(Path.GetTempPath(), "bmbb-" + hash + ".sock");
        return Encoding.UTF8.GetByteCount(inTemp) <= MaxSocketPathBytes ? inTemp : null;
    }

    /// <summary>The instance if this process got the lock; null if another instance of the same folder holds it.</summary>
    public static FileLockSingleInstance? TryAcquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var folder = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(folder);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(folder, LockFileName), options);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }

        var instance = new FileLockSingleInstance(stream, SocketPathFor(folder));
        instance.StartListening();
        return instance;
    }

    /// <summary>
    /// What .NET throws when another process holds the lock: an <see cref="IOException"/> for a sharing violation (on Unix the
    /// <c>flock</c> refusal). Any other I/O failure is a real problem and is not mistaken for "another instance is running".
    /// </summary>
    internal static bool IsSharingViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) == 32 || ex.HResult == unchecked((int)0x80070020) ||
        ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("another process", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the running instance of a data folder to show its window. False if nobody answers.</summary>
    public static bool SignalRunningInstance(string dataDirectory)
    {
        var path = SocketPathFor(dataDirectory);
        if (path is null || !File.Exists(path)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = (int)ClientTimeout.TotalMilliseconds,
                ReceiveTimeout = (int)ClientTimeout.TotalMilliseconds,
            };
            using var cts = new CancellationTokenSource(ClientTimeout);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token).AsTask().GetAwaiter().GetResult();
            socket.Send(Encoding.ASCII.GetBytes(ShowCommand));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Calls <paramref name="onActivate"/> (on a pool thread) every time a second start asks for the window. A request that arrived
    /// before this call is delivered now. A handler that throws is reported to <paramref name="onError"/>; it never ends the listener.
    /// </summary>
    public void Listen(Action onActivate, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        bool deliverNow;
        lock (_gate)
        {
            _onActivate = onActivate;
            _onError = onError;
            deliverNow = _pending;
            _pending = false;
        }
        if (deliverNow) Raise();
    }

    private void StartListening()
    {
        if (_socketPath is null)
        {
            ActivationUnavailable = "No socket path short enough for this folder.";
            return;
        }
        try
        {
            // We hold the lock, so a socket file that is still there belongs to a process that is gone.
            if (File.Exists(_socketPath)) File.Delete(_socketPath);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(4);
            _listener = socket;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(socket));
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            ActivationUnavailable = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task AcceptLoopAsync(Socket listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (_stop.IsCancellationRequested) return;
                await Task.Delay(100);
                continue;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(Socket client)
    {
        try
        {
            using (client)
            {
                var buffer = new byte[16];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(ClientTimeout);
                var received = 0;
                while (received < buffer.Length)
                {
                    var n = await client.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, timeout.Token);
                    if (n == 0) break;
                    received += n;
                    if (Array.IndexOf(buffer, (byte)'\n', 0, received) >= 0) break;
                }
                if (Encoding.ASCII.GetString(buffer, 0, received) == ShowCommand) OnShow();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // a stray or slow connection: nothing to show
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void OnShow()
    {
        bool call;
        lock (_gate)
        {
            call = _onActivate is not null;
            if (!call) _pending = true;
        }
        if (call) Raise();
    }

    private void Raise()
    {
        Action? handler;
        lock (_gate) handler = _onActivate;
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void Report(Exception ex)
    {
        Action<Exception>? onError;
        lock (_gate) onError = _onError;
        try { onError?.Invoke(ex); }
        catch (Exception) { /* reporting is presentation */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        try { _listener?.Dispose(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { }
        if (_listener is not null && _socketPath is not null)
        {
            try { if (File.Exists(_socketPath)) File.Delete(_socketPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _lock.Dispose();
        _stop.Dispose();
    }
}
