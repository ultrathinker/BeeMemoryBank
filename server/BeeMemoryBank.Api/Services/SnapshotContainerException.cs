namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The database entry of a snapshot cannot be opened: its container is damaged, cut short, or was made by another vault (an AES-GCM message that does not
/// authenticate says nothing more than that). The sentence is the same for all three and carries nothing of the exception behind it (a path, the text of a
/// parser); the exception travels as <see cref="Exception.InnerException"/>. It is an <see cref="InvalidOperationException"/> so that every route that already
/// turns a refused restore into a 4xx answer turns this one into it too; nothing was changed when it is thrown.
/// </summary>
public sealed class SnapshotContainerException(Exception? cause = null) : InvalidOperationException(
    "This snapshot's database cannot be opened: it is damaged, cut short, or was made by another vault. Nothing was changed.", cause);
