using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Web.Services;

/// <summary>An Android blind node's backup file is a backup to restore, not a profile to open (by its magic, not its name).</summary>
public sealed class AndroidBackupFileRecognizer : IBackupFileRecognizer
{
    public bool Recognizes(string fullPath) => AndroidBackupRestore.LooksLikeBackup(fullPath);
}
