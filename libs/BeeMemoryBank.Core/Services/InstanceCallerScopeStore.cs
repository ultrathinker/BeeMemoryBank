using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Default ICallerScopeStore: holds scope in an instance field.
/// Registered as scoped, so each DI scope gets its own holder; inside a single DI scope
/// every lookup sees the same value. Suitable for non-HTTP contexts (tests, sync, CLI).
/// </summary>
public sealed class InstanceCallerScopeStore : ICallerScopeStore
{
    public ICallerScope Scope { get; set; } = SystemCallerScope.Instance;
}
