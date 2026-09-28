namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Tells "Open an existing profile" that a chosen FILE is a backup to restore (an Android blind node's
/// backup file), not a profile folder to copy. Registered by the module that restores it; the
/// Api side is <c>IBackupFileRestoreSource</c>.
/// </summary>
public interface IBackupFileRecognizer
{
    bool Recognizes(string fullPath);
}
