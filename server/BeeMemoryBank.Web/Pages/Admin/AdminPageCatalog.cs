namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// One page of the Admin area: where it lives, which section of the index lists it, and the icon, title and short description the index shows. The page
/// itself shows the same icon and title through its template (<c>_AdminPage</c>) and adds its own, longer description.
/// </summary>
public sealed record AdminPageInfo(string Route, string Section, string Icon, string Title, string Summary)
{
    /// <summary>The name of the page: its file name without the extension (<c>Snapshots</c> for <c>/Admin/Snapshots</c>).</summary>
    public string Name => Route[(Route.LastIndexOf('/') + 1)..];
}

/// <summary>The routes of the pages of the Admin area, once, so that a link and a redirect cannot misspell one.</summary>
public static class AdminRoutes
{
    public const string Index = "/Admin";

    public const string RestoredDevices = "/Admin/RestoredDevices";
    public const string NodeIdentity = "/Admin/NodeIdentity";
    public const string TrustedNodes = "/Admin/TrustedNodes";
    public const string ConnectDevice = "/Admin/ConnectDevice";
    public const string DevicesOnMyNetwork = "/Admin/DevicesOnMyNetwork";
    public const string InvisibleMode = "/Admin/InvisibleMode";
    public const string BlindNodes = "/Admin/BlindNodes";

    public const string Snapshots = "/Admin/Snapshots";
    public const string Compaction = "/Admin/Compaction";

    public const string MasterPassword = "/Admin/MasterPassword";
    public const string RecoveryKey = "/Admin/RecoveryKey";
    public const string LockServer = "/Admin/LockServer";
    public const string WebSession = "/Admin/WebSession";
    public const string StorageProtection = "/Admin/StorageProtection";
    public const string OsAutoUnlock = "/Admin/OsAutoUnlock";
    public const string MetadataEncryption = "/Admin/MetadataEncryption";
    public const string DekRotation = "/Admin/DekRotation";

    public const string Agents = "/Admin/Agents";
    public const string AiChat = "/Admin/AiChat";
    public const string SearchDiagnostics = "/Admin/SearchDiagnostics";
    public const string TagGraph = "/Admin/TagGraph";

    public const string SoftwareUpdates = "/Admin/SoftwareUpdates";
    public const string Branding = "/Admin/Branding";

    public const string HardDelete = "/Admin/HardDelete";
    public const string ResetNode = "/Admin/ResetNode";
}

/// <summary>
/// The list of the pages of the Admin area. The index renders it, the template looks the title and the icon of a page up in it, and the tests enumerate it: a
/// page that is not here is not on the index (and its template refuses to render), a row that has no page is a dead link.
/// </summary>
public static class AdminPageCatalog
{
    public const string NodesAndNetwork = "Nodes and network";
    public const string BackupsAndHistory = "Backups and history";
    public const string Security = "Security";
    public const string SearchAndAi = "Search and AI";
    public const string SoftwareAndAppearance = "Software and appearance";
    public const string DangerZone = "Danger zone";

    /// <summary>The sections of the index, in the order it shows them, each with a line on what its pages have in common.</summary>
    public static readonly IReadOnlyList<(string Title, string Hint)> Sections =
    [
        (NodesAndNetwork, "Who is in this network, how the devices reach each other, and how to add one."),
        (BackupsAndHistory, "Copies of this node, and the log of changes the nodes sync with."),
        (Security, "Passwords, keys and encryption."),
        (SearchAndAi, "What AI agents and the chat may use, and how search is doing."),
        (SoftwareAndAppearance, "The version this server runs and the name it shows."),
        (DangerZone, "Things that cannot be undone."),
    ];

    public static readonly IReadOnlyList<AdminPageInfo> All =
    [
        new(AdminRoutes.RestoredDevices, NodesAndNetwork, "shield-exclamation", "Restored devices awaiting confirmation",
            "Devices this node trusted before a restore. They stay switched off until you confirm that you recognise them."),
        new(AdminRoutes.NodeIdentity, NodesAndNetwork, "fingerprint", "Node Identity",
            "The ID, name and public key that identify this node to the rest of the network."),
        new(AdminRoutes.TrustedNodes, NodesAndNetwork, "people", "Trusted Nodes",
            "The devices that belong to this network: who may sync with this node, who is a superadmin and what each may apply on its own."),
        new(AdminRoutes.ConnectDevice, NodesAndNetwork, "phone", "Connect a device",
            "Let a phone or another computer join this one for 15 minutes with a join code. Nothing stays open afterwards."),
        new(AdminRoutes.DevicesOnMyNetwork, NodesAndNetwork, "wifi", "Devices on my network",
            "Open this node to the other devices on your local network, or keep it answering this computer only."),
        new(AdminRoutes.InvisibleMode, NodesAndNetwork, "eye-slash", "Invisible Mode",
            "Make this node call nobody and answer nobody, without losing anything: your work is delivered when you turn it off."),
        new(AdminRoutes.BlindNodes, NodesAndNetwork, "shield-lock", "Blind nodes",
            "Keep an encrypted copy of this network and its backups on another machine that cannot read it."),

        new(AdminRoutes.Snapshots, BackupsAndHistory, "archive", "Snapshots",
            "Backups of this node: create one, download or upload a file, restore this node or the whole network from it."),
        new(AdminRoutes.Compaction, BackupsAndHistory, "journal-text", "Event Log & Compaction",
            "Shorten the log of changes this node keeps for syncing, and see how far each device has caught up."),

        new(AdminRoutes.MasterPassword, Security, "key", "Master Password",
            "Change the password that opens this node's copy of the vault key."),
        new(AdminRoutes.RecoveryKey, Security, "life-preserver", "Recovery key",
            "Issue a key that gets you back in if the administrator password is lost."),
        new(AdminRoutes.LockServer, Security, "lock", "Lock Server",
            "Erase the vault key from memory and sign everybody out. A superadmin signing in, an enabled agent key or OS auto-unlock after a restart opens it again."),
        new(AdminRoutes.WebSession, Security, "clock-history", "Web Session",
            "How long a sign-in in the browser lasts, and whether being active extends it."),
        new(AdminRoutes.StorageProtection, Security, "database-lock", "Database encryption",
            "Encrypt the database files of the node, so that a stolen disk or a copy of the data folder shows nothing."),
        new(AdminRoutes.OsAutoUnlock, Security, "unlock", "OS Auto-Unlock",
            "Let this computer open the vault at startup without a password, through your operating-system account."),
        new(AdminRoutes.MetadataEncryption, Security, "incognito", "Metadata encryption",
            "Encrypt titles, folder and tag names and the shape of the tree for the whole network. One way."),
        new(AdminRoutes.DekRotation, Security, "shield-lock", "DEK Rotation",
            "Replace the key that encrypts every article, version and file. A heavy operation for the whole network."),

        new(AdminRoutes.Agents, SearchAndAi, "robot", "Agents",
            "The keys AI agents use to reach this vault: see when each was last used and revoke the ones you no longer trust."),
        new(AdminRoutes.AiChat, SearchAndAi, "chat-left-text", "AI / Chat",
            "The OpenRouter keys and models behind the built-in AI chat, and whether users may use it at all."),
        new(AdminRoutes.SearchDiagnostics, SearchAndAi, "search", "Search diagnostics",
            "How fast searches are, how healthy the search index is, and the switch for meaning-based (semantic) search."),
        new(AdminRoutes.TagGraph, SearchAndAi, "share", "Tag graph",
            "Check the lookup behind the Graph view and related tags, and rebuild it if it has drifted."),

        new(AdminRoutes.SoftwareUpdates, SoftwareAndAppearance, "arrow-up-circle", "Software Updates",
            "See which version this node runs, and check for, download and apply an update."),
        new(AdminRoutes.Branding, SoftwareAndAppearance, "type", "Branding",
            "Change the product name shown in the header and in the browser tab of this node."),

        new(AdminRoutes.HardDelete, DangerZone, "trash", "Hard Delete",
            "Permanently remove folders, articles and their files from this node and from every synced node. There is no undo."),
        new(AdminRoutes.ResetNode, DangerZone, "exclamation-octagon", "Reset This Node",
            "Wipe everything on this node and return it to the first-run Setup screen."),
    ];

    /// <summary>The page with this route (<c>/Admin/Snapshots</c>, any case), or null.</summary>
    public static AdminPageInfo? Find(string? route) =>
        string.IsNullOrEmpty(route) ? null : All.FirstOrDefault(p => string.Equals(p.Route, route, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pages of one section, in the order of the list.</summary>
    public static IEnumerable<AdminPageInfo> In(string section) => All.Where(p => p.Section == section);

    /// <summary>
    /// Where each card of the old, single Admin page went. A bookmark <c>/Admin#card-snapshots</c> still reaches the index (a fragment never reaches the
    /// server); the script of the index reads this table and moves the browser to the page of the block. Never remove a row: the ids are in bookmarks.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyAnchors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["card-updates"] = AdminRoutes.SoftwareUpdates,
        ["card-master-password"] = AdminRoutes.MasterPassword,
        ["devices-on-my-network"] = AdminRoutes.DevicesOnMyNetwork,
        ["card-invisible-mode"] = AdminRoutes.InvisibleMode,
        ["card-trusted-nodes"] = AdminRoutes.TrustedNodes,
        ["restored-peers"] = AdminRoutes.RestoredDevices,
        ["card-snapshots"] = AdminRoutes.Snapshots,
        ["card-compaction"] = AdminRoutes.Compaction,
        ["card-agents"] = AdminRoutes.Agents,
        ["card-metadata"] = AdminRoutes.MetadataEncryption,
        ["card-database-encryption"] = AdminRoutes.StorageProtection,
        ["card-os-auto-unlock"] = AdminRoutes.OsAutoUnlock,
        ["card-dek-rotation"] = AdminRoutes.DekRotation,
        ["card-reset-node"] = AdminRoutes.ResetNode,
        ["card-diagnostics"] = AdminRoutes.SearchDiagnostics,
    };
}
