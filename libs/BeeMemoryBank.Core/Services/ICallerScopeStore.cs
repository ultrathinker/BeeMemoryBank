using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Backing storage for the caller's ICallerScope. Implementations decide where scope
/// state lives so it survives DI scope boundaries:
/// - InstanceCallerScopeStore: holds scope in an instance field. Suitable for tests, CLI,
///   background jobs — anywhere there's no HTTP request.
/// - HttpContextCallerScopeStore (Api project): reads/writes HttpContext.Items so the scope
///   is shared across any child DI scope created within the same HTTP request (e.g. the
///   MCP server SDK creates a child scope per tool invocation — a plain scoped holder
///   would not see the scope set by middleware in that case, silently bypassing ACL).
/// </summary>
public interface ICallerScopeStore
{
    ICallerScope Scope { get; set; }
}
