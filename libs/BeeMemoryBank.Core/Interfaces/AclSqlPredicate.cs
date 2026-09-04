namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// A self-contained, parameterized SQL boolean expression plus the parameters it references.
/// Produced by <see cref="ICallerScope.BuildReadAclPredicate"/> and
/// <see cref="ICallerScope.BuildFolderVisibilityPredicate"/> so deny/allow-prefix ACL filtering
/// can run IN the database (pushed into a WHERE clause) instead of after a full in-memory load.
///
/// <para>
/// <see cref="Sql"/> is a boolean expression (no leading "AND"/"WHERE") safe to splice directly
/// into a WHERE clause, e.g. <c>$"... WHERE status = 'A' AND ({predicate.Sql})"</c>.
/// <see cref="Parameters"/> holds every bound-parameter name (WITHOUT the leading "@") this SQL
/// references, keyed exactly as they appear in <see cref="Sql"/>.
/// </para>
///
/// <para>
/// This type deliberately carries no Dapper/ADO.NET dependency (BeeMemoryBank.Core does not
/// reference Dapper) — callers in BeeMemoryBank.Storage merge <see cref="Parameters"/> into
/// whatever parameter object/DynamicParameters they are already building for the query.
/// </para>
/// </summary>
public sealed record AclSqlPredicate(string Sql, IReadOnlyDictionary<string, object?> Parameters);
