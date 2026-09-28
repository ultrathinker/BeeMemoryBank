namespace BeeMemoryBank.Rekey;

/// <summary>
/// The steps of a re-key, in order (rekey-offline.md §8.2). L's file: each owner adds a step class under
/// <c>Steps/</c> and L lists it here.
/// <list type="number">
/// <item>KeyMaterialStep (L): D_c, sentinel, owner slot, other slots, agents, chain material, retired-key rows,
///   recovery boxes and links.</item>
/// <item>RowResealStep (R1): bodies, versions, conflicts, comments, media (with <c>.enc</c> files moved into blobs),
///   sealed secrets, remote tokens.</item>
/// <item>ChatRekeyStep (R2): chat.db rows under the new chat key, and its <c>chat</c> row under D_c.</item>
/// <item>DerivedDataClearStep (R1): index state, embeddings, chunk embeddings, tag vectors.</item>
/// <item>PeerRevokeStep and EventLogResetStep (L): other nodes revoked; the event log cleared with a fresh
///   checkpoint; quarantine cleared.</item>
/// </list>
/// </summary>
public static class RekeyPlan
{
    /// <summary>The pre-flight (R2).</summary>
    public static Func<IRekeyPreflight>? Preflight { get; set; }

    /// <summary>The steps, in order. Filled as the step classes land.</summary>
    public static IReadOnlyList<Func<IRekeyStep>> Steps { get; } = [];
}
