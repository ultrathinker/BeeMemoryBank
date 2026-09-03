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

    /// <summary>
    /// Temporarily runs as <see cref="SystemCallerScope"/> — no folder ACL, no read-only guard —
    /// restoring the caller's real scope when the returned value is disposed:
    /// <code>using var _ = scopeHolder.ElevateToSystem();</code>
    ///
    /// <para>Elevation used to be written out by hand at every site: assign
    /// <c>Scope = SystemCallerScope.Instance</c>, then remember a try/finally that puts the old
    /// value back. Forgetting the finally does not fail visibly — it leaks full-vault access to
    /// whatever runs next on the same scope store, and under
    /// <c>HttpContextCallerScopeStore</c> that is the remainder of the HTTP request, including
    /// any user-facing work after the elevated section. This shape makes the restore part of the
    /// language construct instead of a convention.</para>
    ///
    /// <para>Elevate only around work that is genuinely system-owned (replaying a peer's events,
    /// resolving a user's own ACL rows, bootstrapping system folders) and keep the block as small
    /// as the work requires.</para>
    /// </summary>
    public ScopeElevation ElevateToSystem() => new(this, SystemCallerScope.Instance);

    /// <summary>Restores the previous <see cref="CallerScopeHolder.Scope"/> on dispose.</summary>
    public readonly struct ScopeElevation : IDisposable
    {
        private readonly CallerScopeHolder _holder;
        private readonly ICallerScope _previous;

        internal ScopeElevation(CallerScopeHolder holder, ICallerScope elevated)
        {
            _holder = holder;
            _previous = holder.Scope;
            holder.Scope = elevated;
        }

        public void Dispose()
        {
            // Guarded because `default(ScopeElevation)` is constructible and would otherwise NRE
            // here; restoring is idempotent, so a copied struct disposing twice is harmless.
            if (_holder != null) _holder.Scope = _previous;
        }
    }
}
