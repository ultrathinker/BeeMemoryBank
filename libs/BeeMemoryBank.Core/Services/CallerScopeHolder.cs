using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Ambient caller scope. Repositories and services depend on this type to filter reads
/// and validate writes against folder ACL. The actual storage is delegated to
/// <see cref="ICallerScopeStore"/> so that the holder is resilient to DI scope boundaries
/// (see the XML doc on ICallerScopeStore for why that matters).
/// </summary>
public class CallerScopeHolder
{
    private readonly ICallerScopeStore _store;

    /// <summary>
    /// Parameterless ctor for tests and direct construction outside of DI. Uses an
    /// in-memory InstanceCallerScopeStore that behaves identically to pre-refactor.
    /// </summary>
    public CallerScopeHolder() : this(new InstanceCallerScopeStore()) { }

    public CallerScopeHolder(ICallerScopeStore store)
    {
        _store = store;
    }

    public ICallerScope Scope
    {
        get => _store.Scope;
        set => _store.Scope = value;
    }
}
