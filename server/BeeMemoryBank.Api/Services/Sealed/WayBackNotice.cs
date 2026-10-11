namespace BeeMemoryBank.Api.Services.Sealed;

/// <summary>
/// The sentence a password change and a key rotation add to their confirmation while the way-back snapshot of "Encrypt this node" exists
/// (docs/design/step-b-spec.md 10.7): the snapshot opens with the password and recovery key of the day it was taken, whatever happened since.
/// </summary>
public static class WayBackNotice
{
    public const string Sentence = "The way-back snapshot still opens with your old password. Delete it if you no longer need it.";

    /// <summary>The sentence while <c>pre-encryption/</c> holds a snapshot; null otherwise, so that the confirmation of a node without one is what it was.</summary>
    public static string? For(IServiceProvider services)
    {
        var folder = services.GetService<SnapshotService>()?.PreEncryptionDir;
        return folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder, "bmb-snapshot-*.tar.gz").Any() ? Sentence : null;
    }
}
