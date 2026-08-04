namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// The repository layer enforces per-caller folder/article write scope by throwing
/// ReadOnlyAccessException (path is read-only, carries Path) or plain UnauthorizedAccessException
/// (path is outside the caller's scope) — see ICallerScope.IsReadOnly/IsAccessDenied. MCP tools,
/// the chat tool loop, and REST endpoints each catch these and build their own caller-facing
/// message; this centralizes the "which kind is it" dispatch so every call site only supplies
/// its own wording, not the exception-type plumbing — and can't get the catch-order-matters
/// subtlety wrong, since ReadOnlyAccessException derives from UnauthorizedAccessException.
/// Unrelated uses of UnauthorizedAccessException elsewhere (wrong password, sync node trust) are
/// out of scope for this classifier — don't route those through it.
/// </summary>
public enum WriteAclDenialKind
{
    ReadOnly,
    Unauthorized
}

public static class WriteAclDenial
{
    public static bool TryClassify(Exception ex, out WriteAclDenialKind kind, out string? path)
    {
        switch (ex)
        {
            case BeeMemoryBank.Core.Services.ReadOnlyAccessException ro:
                kind = WriteAclDenialKind.ReadOnly;
                path = ro.Path;
                return true;
            case UnauthorizedAccessException:
                kind = WriteAclDenialKind.Unauthorized;
                path = null;
                return true;
            default:
                kind = default;
                path = null;
                return false;
        }
    }
}
