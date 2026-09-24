namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// The request cannot proceed because of the current state of the vault, not because of anything
/// wrong with the request itself: something else holds the execute lock, a rotation is already
/// pending, a name is already taken. Maps to HTTP 409.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> on purpose: dozens of
/// <c>catch (InvalidOperationException)</c> blocks across the API endpoints and the MCP tools
/// translate these into their own responses. Keeping the base type means converting a throw site
/// changes nothing for a handler that does not catch the derived type — only handlers that opt
/// into it see a difference.
/// </remarks>
public class ConflictException : InvalidOperationException
{
    public ConflictException(string message) : base(message)
    {
    }

    public ConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
