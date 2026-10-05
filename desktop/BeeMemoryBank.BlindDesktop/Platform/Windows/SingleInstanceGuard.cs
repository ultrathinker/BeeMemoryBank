using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.BlindDesktop.Platform;

/// <summary>
/// One blind app per user and data folder. The first instance owns a named mutex of its own (never the full app's) and listens on a
/// named event; a second start finds the mutex taken, raises the event so that the first one shows its window, and exits.
/// This is the Windows implementation of <see cref="IInstanceGuard"/> (named mutexes and events do not exist in .NET on Unix).
/// </summary>
internal sealed class SingleInstanceGuard : IInstanceGuard
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _listener;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>The mutex name for a data folder: per folder (so per user and per profile), and apart from every name the full app uses.</summary>
    public static string MutexNameFor(string dataDirectory) => "Global\\BeeMemoryBankBlind.Instance." + Key(dataDirectory);

    /// <summary>The name of the event the second instance raises to bring the first one's window forward.</summary>
    public static string ActivateNameFor(string dataDirectory) => "Global\\BeeMemoryBankBlind.Activate." + Key(dataDirectory);

    /// <summary>The guard if this process is the first instance; null if another one already runs.</summary>
    public static SingleInstanceGuard? TryAcquire(string dataDirectory)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexNameFor(dataDirectory), out var first);
        if (!first)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstanceGuard(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ActivateNameFor(dataDirectory)));
    }

    /// <summary>Asks the running instance (if any) to show its window.</summary>
    public static bool SignalRunningInstance(string dataDirectory)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateNameFor(dataDirectory), out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a pool thread) every time a second start asks for the window.</summary>
    public void Listen(Action onActivate)
    {
        // The callback runs on a thread-pool thread: an exception that leaves it ends the process, so none does.
        _listener = ThreadPool.RegisterWaitForSingleObject(_activate, (_, timedOut) =>
        {
            if (timedOut) return;
            try
            {
                onActivate();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Showing the window for a second start failed", ex);
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }

    private static string Key(string dataDirectory)
    {
        var normal = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal)))[..16];
    }
}
