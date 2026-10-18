namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// The id of the heading of each card of the Admin page. A handler ends with <c>Back(card, ...)</c>: the page opens again at that fragment, and the
/// message of the action shows in that card (BMB-303).
/// </summary>
public static class AdminCards
{
    public const string Updates = "card-updates";
    public const string MasterPassword = "card-master-password";
    public const string Devices = "devices-on-my-network";
    public const string InvisibleMode = "card-invisible-mode";
    public const string TrustedNodes = "card-trusted-nodes";
    public const string RestoredPeers = "restored-peers";
    public const string Snapshots = "card-snapshots";
    public const string Compaction = "card-compaction";
    public const string Agents = "card-agents";
    public const string Metadata = "card-metadata";
    public const string DatabaseEncryption = "card-database-encryption";
    public const string OsAutoUnlock = "card-os-auto-unlock";
    public const string DekRotation = "card-dek-rotation";
    public const string ResetNode = "card-reset-node";
    public const string Diagnostics = "card-diagnostics";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Updates, MasterPassword, Devices, InvisibleMode, TrustedNodes, RestoredPeers, Snapshots, Compaction, Agents, Metadata, DatabaseEncryption, OsAutoUnlock,
        DekRotation, ResetNode, Diagnostics,
    };
}

/// <summary>What <c>_CardMessage</c> needs: the page, and the card that asks.</summary>
public sealed record AdminCardMessage(AdminModel Page, string Card);
