namespace BeeMemoryBank.Api.Services;

/// <summary>
/// A restore that the rule of the product refuses, with a sentence the owner can act on (BMB-306): nothing was changed. It is an
/// <see cref="InvalidOperationException"/> so that every route that already turns a refused restore into a 4xx answer turns this one into it too.
/// </summary>
public sealed class RestoreRefusedException(string message) : InvalidOperationException(message)
{
    /// <summary>The refusal of a copy that holds the notes in the open (made before metadata encryption was on): no restore reads one.</summary>
    public const string OpenCopyMessage =
        "This copy was made before metadata encryption was switched on, so it holds your notes in the open, and a restore never reads an unencrypted copy " +
        "(it never produces an unencrypted node either). Restore from a copy made after the switch - one a node of a network that has metadata encryption on " +
        "made - or join a node that has it on. Nothing was changed.";

    /// <summary>The refusal of a restore in a host that cannot end it encrypted (the sealed services are not part of its composition): it would leave the restored node open, with no marker for the next start.</summary>
    public const string CannotEncryptMessage =
        "This host cannot encrypt a node (the services that do are not part of it), and a restore never produces an unencrypted node, so it was refused before " +
        "any key was recovered or anything was written. Nothing was changed.";

    /// <summary>The refusal of a restore into a node whose database is not encrypted: no restore, of any kind, produces an open node.</summary>
    public const string PlainNodeMessage =
        "This node's database is not encrypted, and a restore never produces an unencrypted node. " +
        "Encrypt this node first (Admin, Database encryption), then restore. Nothing was changed.";
}
