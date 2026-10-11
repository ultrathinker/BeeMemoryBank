namespace BeeMemoryBank.Core.Services;

/// <summary>The scope of an <see cref="Interfaces.IEventLogger"/> that holds nothing back (no sync, or no sealing).</summary>
public sealed class NullAuthoringScope : IDisposable
{
    public static readonly NullAuthoringScope Instance = new();

    private NullAuthoringScope()
    {
    }

    public void Dispose()
    {
    }
}
