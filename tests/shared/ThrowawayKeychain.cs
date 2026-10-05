using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Platforms.Apple.TestSupport;

/// <summary>
/// A keychain FILE made for one test: created through Security.framework (SecKeychainCreate), which - unlike `security create-keychain` -
/// does not add it to the user's keychain search list, protected by a random password, unlocked, never prompting. The code under test is
/// pointed at it, so the login keychain is never reached. Disposing removes only this file and the folder this fixture made for it.
/// One source, compiled into every test project that needs a throwaway keychain (tests/shared).
/// </summary>
internal sealed class ThrowawayKeychain : IDisposable
{
    private readonly string _folder;
    private readonly byte[] _password;
    private IntPtr _keychain;

    public string Path { get; }

    public ThrowawayKeychain()
    {
        NativeLibraries.RequireMacOS();
        _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bmb-apple-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);
        Path = System.IO.Path.Combine(_folder, "test.keychain");
        _password = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var status = Sec.SecKeychainCreate(Sec.CPath(Path), (uint)_password.Length, _password, promptUser: false, IntPtr.Zero, out _keychain);
        if (status != 0) throw new InvalidOperationException($"SecKeychainCreate failed with status {status}: {Sec.Describe(status)}");
        Unlock();
    }

    /// <summary>Locks the keychain: the next item call needs the password, which a call that may not prompt cannot get.</summary>
    public void Lock()
    {
        var status = Sec.SecKeychainLock(_keychain);
        if (status != 0) throw new InvalidOperationException($"SecKeychainLock failed with status {status}: {Sec.Describe(status)}");
    }

    public void Unlock()
    {
        var status = Sec.SecKeychainUnlock(_keychain, (uint)_password.Length, _password, usePassword: true);
        if (status != 0) throw new InvalidOperationException($"SecKeychainUnlock failed with status {status}: {Sec.Describe(status)}");
    }

    public void Dispose()
    {
        if (_keychain != IntPtr.Zero)
        {
            // Deletes the file this test made (a keychain that is in no search list), then the reference.
            Sec.SecKeychainDelete(_keychain);
            CF.CFRelease(_keychain);
            _keychain = IntPtr.Zero;
        }
        CryptographicOperations.ZeroMemory(_password);
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
