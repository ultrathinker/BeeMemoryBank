namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The one operating-system lock on the app's data root: a file, <c>accounts.lock</c>, held open with <see cref="FileShare.None"/>. The locks inside the code (a
/// <c>lock</c> statement, a semaphore) keep the threads of ONE process apart; two processes (a second copy of the app, a tool, a head started twice) and two hosts in one
/// process are kept apart by this file. A <see cref="BlindAccountHost"/> holds it for as long as it lives, so a second host on the same root is refused with a sentence and
/// never composes an account the first one runs. A registry used without a host takes it for the length of one update (load, change, write) and gives it back.
///
/// <para>The lock belongs to the open file, so the operating system lets go of it when the process dies, however it dies: there is no stale lock to clear. The file holds
/// nothing and stays in the folder.</para>
/// </summary>
internal sealed class BlindRootLock : IDisposable
{
    public const string FileName = "accounts.lock";

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(25);
    private FileStream? _stream;

    private BlindRootLock(FileStream stream) => _stream = stream;

    /// <summary>The sentence a refused lock gives: what to do, nothing of the machine.</summary>
    public const string RefusedSentence =
        "Another copy of this app is already using this data folder. Close it and open this one again. Nothing was started or changed.";

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="wait"/> for a holder to let go. Null with <paramref name="problem"/> when another holder keeps it, when the file cannot be
    /// opened for any other reason, or when the folder is not a safe one (a link: the lock file would be made somewhere else).
    /// </summary>
    public static BlindRootLock? TryAcquire(string root, TimeSpan wait, out string? problem)
    {
        problem = null;
        if (BlindPathSafety.RootProblem(root) is { } unsafeRoot)
        {
            problem = unsafeRoot;
            return null;
        }

        var path = Path.Combine(root, FileName);
        if (BlindPathSafety.Occupied(path) && (BlindPathSafety.IsLink(path) || !File.Exists(path)))
        {
            problem = "accounts.lock is a link or a folder, not the file this app locks. Remove it by hand.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The folder this app keeps its data in could not be opened (" + ex.GetType().Name + "). Nothing was started or changed.";
            return null;
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new BlindRootLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
            }
            catch (UnauthorizedAccessException ex)
            {
                problem = "The lock file of this app could not be opened (" + ex.GetType().Name + "). Nothing was started or changed.";
                return null;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(RetryEvery);
            }
            catch (IOException)
            {
                problem = RefusedSentence;
                return null;
            }
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
