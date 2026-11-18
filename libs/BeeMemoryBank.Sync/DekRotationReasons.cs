using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Why a DEK rotation was started, as its <c>reason</c> member says (release 2.12, plan WS-A5). The only reason with a meaning is a
/// removal: <c>offboarding:&lt;process id&gt;</c>, the rotation that ends the removal of a person. Such a rotation, signed by a node every
/// receiver sees as a superadmin, is applied by every unlocked full node whatever its <c>auto_accept_dek_rotation</c> says, and by a locked one
/// at its next unlock: nobody may keep the old key of a person who left by declining a rotation. Every other rotation keeps the rule it had.
/// The member travels in clear and names no person.
/// </summary>
public static partial class DekRotationReasons
{
    public const string OffboardingPrefix = "offboarding:";

    [GeneratedRegex("^offboarding:[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RemovalShape();

    /// <summary>True for no reason at all and for a well-formed removal reason; anything else is refused by the propose API.</summary>
    public static bool IsValid(string? reason) => reason is null || RemovalShape().IsMatch(reason);

    /// <summary>True when <paramref name="reason"/> names a removal process.</summary>
    public static bool IsRemoval(string? reason) => reason is not null && RemovalShape().IsMatch(reason);

    /// <summary>The reason a commit event carries, or null (no reason, or a payload that does not parse).</summary>
    public static string? Of(SyncEvent commit)
    {
        try
        {
            return JsonSerializer.Deserialize<DekRotationCommitPayload>(commit.Payload)?.Reason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this node applies the rotation <paramref name="commit"/> without an administrator: a removal rotation from a node this node
    /// sees as a superadmin always (<c>Removal</c> true), any other one when the originator's <c>auto_accept_dek_rotation</c> is on.
    /// </summary>
    public static async Task<(bool Apply, bool Removal)> DecideAutoApplyAsync(SyncEvent commit, IWhitelistRepository whitelist)
    {
        if (IsRemoval(Of(commit)) && (await whitelist.GetByNodeIdAsync(commit.NodeId))?.IsSuperadmin == true)
            return (true, true);
        return (await whitelist.GetAutoAcceptDekRotationAsync(commit.NodeId.ToString()), false);
    }
}
