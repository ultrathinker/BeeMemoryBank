namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// One page of the Admin area: where it lives, which section of the index lists it, and the icon, title and short description the index shows. The page
/// itself shows the same icon and title through its template (<c>_AdminPage</c>) and adds its own, longer description.
/// </summary>
/// <param name="Listed">False for a page that is reached from another page of the area and has no row on the index: the details of one device (it needs a device)
/// and the page that adds a blind node (a button of the Devices page).</param>
public sealed record AdminPageInfo(string Route, string Section, string Icon, string Title, string Summary, bool Listed = true)
{
    /// <summary>The name of the page: its file name without the extension (<c>Snapshots</c> for <c>/Admin/Snapshots</c>).</summary>
    public string Name => Route[(Route.LastIndexOf('/') + 1)..];
}

/// <summary>The routes of the pages of the Admin area, once, so that a link and a redirect cannot misspell one.</summary>
public static class AdminRoutes
{
    public const string Index = "/Admin";

    public const string Devices = "/Admin/Devices";
    /// <summary>The details of one device: <c>/Admin/Device/{nodeId}</c>.</summary>
    public const string Device = "/Admin/Device";
    public const string NetworkSettings = "/Admin/NetworkSettings";
    /// <summary>"Add a blind node": the pair code, the invitations and the Android blind copies. The blind nodes themselves are devices of the Devices list.</summary>
    public const string BlindNodes = "/Admin/BlindNodes";

    /// <summary>The address of the details of one device.</summary>
    public static string DeviceOf(Guid nodeId) => Device + "/" + nodeId;

    // Pages of 2.7.5 and earlier that the Devices section replaced. Their routes answer a redirect (a LegacyPageRedirectModel in the file of the old page); nothing links to them any more.
    public const string RestoredDevices = "/Admin/RestoredDevices";
    public const string NodeIdentity = "/Admin/NodeIdentity";
    public const string TrustedNodes = "/Admin/TrustedNodes";
    public const string ConnectDevice = "/Admin/ConnectDevice";
    public const string DevicesOnMyNetwork = "/Admin/DevicesOnMyNetwork";
    public const string InvisibleMode = "/Admin/InvisibleMode";

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
    public const string IdentityKey = "/Admin/IdentityKey";
    /// <summary>The removals of superadmins (release 2.12.0): the list, and one process with <c>?id=</c>.</summary>
    public const string Offboarding = "/Admin/Offboarding";
    public static string OffboardingOf(Guid processId) => Offboarding + "?id=" + processId.ToString("D");

    public const string Agents = "/Admin/Agents";
    public const string AiChat = "/Admin/AiChat";
    public const string Dream = "/Admin/Dream";
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
    public const string DevicesSection = "Devices";
    public const string BackupsAndHistory = "Backups and history";
    public const string Security = "Security";
    public const string SearchAndAi = "Search and AI";
    public const string SoftwareAndAppearance = "Software and appearance";
    public const string DangerZone = "Danger zone";

    /// <summary>The sections of the index, in the order it shows them, each with a line on what its pages have in common.</summary>
    public static readonly IReadOnlyList<(string Title, string Hint)> Sections =
    [
        (DevicesSection, "Who is in this network, how the devices reach each other, and how to add one."),
        (BackupsAndHistory, "Copies of this node, and the log of changes the nodes sync with."),
        (Security, "Passwords, keys and encryption."),
        (SearchAndAi, "What AI agents and the chat may use, and how search is doing."),
        (SoftwareAndAppearance, "The version this server runs and the name it shows."),
        (DangerZone, "Things that cannot be undone."),
    ];

    public static readonly IReadOnlyList<AdminPageInfo> All =
    [
        new(AdminRoutes.Devices, DevicesSection, "people", "Devices",
            "Your devices: connect a phone or a computer, see how each one is doing."),
        new(AdminRoutes.NetworkSettings, DevicesSection, "wifi", "Network settings",
            "How this computer talks to your other devices."),
        new(AdminRoutes.Device, DevicesSection, "laptop", "Device",
            "One device and how it is doing.", Listed: false),
        new(AdminRoutes.BlindNodes, DevicesSection, "shield-lock", "Add a blind node",
            "Keep an encrypted backup on another machine that cannot read it.", Listed: false),

        new(AdminRoutes.Snapshots, BackupsAndHistory, "archive", "Snapshots",
            "Make a backup, or restore from one."),
        new(AdminRoutes.Compaction, BackupsAndHistory, "journal-text", "Event Log & Compaction",
            "The history of changes used for syncing, and how far each device has caught up."),

        new(AdminRoutes.MasterPassword, Security, "key", "Master Password",
            "Change your master password."),
        new(AdminRoutes.RecoveryKey, Security, "life-preserver", "Recovery keys",
            "Personal keys that get a superadmin back in if they forget their password. Issue, list and revoke them."),
        new(AdminRoutes.LockServer, Security, "lock", "Lock Server",
            "Lock everything now and sign everybody out."),
        new(AdminRoutes.WebSession, Security, "clock-history", "Web Session",
            "How long you stay signed in."),
        new(AdminRoutes.StorageProtection, Security, "database-lock", "Database encryption",
            "Whether the database on this computer is encrypted."),
        new(AdminRoutes.OsAutoUnlock, Security, "unlock", "OS Auto-Unlock",
            "Open the vault when this computer starts, without typing the password."),
        new(AdminRoutes.MetadataEncryption, Security, "incognito", "Metadata encryption",
            "Encrypt titles, folder names and tags across your devices."),
        new(AdminRoutes.DekRotation, Security, "shield-lock", "DEK Rotation",
            "Replace the key that encrypts your notes. Rarely needed."),
        new(AdminRoutes.IdentityKey, Security, "fingerprint", "Node identity key",
            "The key this node signs with, and how to give it a new one."),
        new(AdminRoutes.Offboarding, Security, "person-x", "People being removed",
            "Superadmins whose rights are being taken away, step by step."),

        new(AdminRoutes.Agents, SearchAndAi, "robot", "Agents",
            "Keys for AI assistants: see them and switch them off."),
        new(AdminRoutes.AiChat, SearchAndAi, "chat-left-text", "AI / Chat",
            "Which AI models the chat uses."),
        new(AdminRoutes.Dream, SearchAndAi, "moon-stars", "Dream",
            "When Dream reviews your notes and which folders it skips."),
        new(AdminRoutes.SearchDiagnostics, SearchAndAi, "search", "Search diagnostics",
            "Search speed and health, and search by meaning."),
        new(AdminRoutes.TagGraph, SearchAndAi, "share", "Tag graph",
            "Check and rebuild the tag graph."),

        new(AdminRoutes.SoftwareUpdates, SoftwareAndAppearance, "arrow-up-circle", "Software Updates",
            "Your version, and updates."),
        new(AdminRoutes.Branding, SoftwareAndAppearance, "type", "Branding",
            "The name shown at the top of the page."),

        new(AdminRoutes.HardDelete, DangerZone, "trash", "Hard Delete",
            "Delete notes forever, on every device. There is no undo."),
        new(AdminRoutes.ResetNode, DangerZone, "exclamation-octagon", "Reset This Node",
            "Erase everything on this computer and start again."),
    ];

    /// <summary>The page with this route (<c>/Admin/Snapshots</c>, any case), or null.</summary>
    public static AdminPageInfo? Find(string? route) =>
        string.IsNullOrEmpty(route) ? null : All.FirstOrDefault(p => string.Equals(p.Route, route, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pages of one section the index lists, in the order of the list.</summary>
    public static IEnumerable<AdminPageInfo> In(string section) => All.Where(p => p.Section == section && p.Listed);

    /// <summary>
    /// The routes of the pages the Devices section replaced. A route here answers a redirect to the new place (a <c>LegacyPageRedirectModel</c> in the
    /// page's own file), so a bookmark, a link in an older app or a message of an older node still lead somewhere. They are not in <see cref="All"/>: they are
    /// no pages of the area. Never remove a row.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyRoutes =
    [
        AdminRoutes.RestoredDevices, AdminRoutes.NodeIdentity, AdminRoutes.TrustedNodes, AdminRoutes.ConnectDevice,
        AdminRoutes.DevicesOnMyNetwork, AdminRoutes.InvisibleMode,
    ];

    /// <summary>
    /// Where each card of the old, single Admin page went. A bookmark <c>/Admin#card-snapshots</c> still reaches the index (a fragment never reaches the
    /// server); the script of the index reads this table and moves the browser to the page of the block (and, after a <c>#</c>, to the block on that page).
    /// Never remove a row: the ids are in bookmarks.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyAnchors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["card-updates"] = AdminRoutes.SoftwareUpdates,
        ["card-master-password"] = AdminRoutes.MasterPassword,
        ["devices-on-my-network"] = AdminRoutes.NetworkSettings + "#devices-on-my-network",
        ["card-invisible-mode"] = AdminRoutes.NetworkSettings + "#invisible-mode",
        ["card-trusted-nodes"] = AdminRoutes.Devices,
        ["restored-peers"] = AdminRoutes.Devices + "#restored-peers",
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
