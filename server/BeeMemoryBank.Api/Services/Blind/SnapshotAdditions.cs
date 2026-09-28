namespace BeeMemoryBank.Api.Services;

/// <summary>
/// What a package built on top of a peer snapshot changes about it (<see cref="SnapshotService.CreateAsync"/>).
/// The blind package (plan 4.2, CONTRACTS §2) is the caller: it keeps tbl_hard_delete_audit, nulls
/// embedding projections and carries blind-manifest.json.
/// </summary>
/// <param name="KeepTables">Tables kept although secret filtering would drop or empty them.</param>
/// <param name="AdjustDb">Runs on the filtered copy of the database (its path), before hashing.</param>
/// <param name="ExtraFiles">Extra archive entries, listed and hashed in the signed manifest.</param>
public sealed record SnapshotAdditions(
    IReadOnlyCollection<string> KeepTables,
    Action<string> AdjustDb,
    IReadOnlyDictionary<string, byte[]> ExtraFiles);
