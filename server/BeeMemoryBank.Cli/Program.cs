using System.CommandLine;
using BeeMemoryBank.Cli.Commands;

var defaultDataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bmb", "data");

var dataOption = new Option<string>(
    "--data",
    () => defaultDataPath,
    "Path to the data directory (or BMB_DATA_PATH)");
dataOption.AddAlias("-d");

var root = new RootCommand("BeeMemoryBank — personal knowledge base with E2E encryption");
root.AddGlobalOption(dataOption);

// ─── bmb init ───────────────────────────────────────────────────────────────

var initNameOpt = new Option<string>("--name", "Node name") { IsRequired = true };
var initPasswordOpt = new Option<string>("--password", "Master password") { IsRequired = true };

var initCmd = new Command("init", "Initial node setup");
initCmd.AddOption(initNameOpt);
initCmd.AddOption(initPasswordOpt);
initCmd.SetHandler(async (data, name, password) =>
{
    Environment.Exit(await InitCommand.HandleAsync(data, name, password));
}, dataOption, initNameOpt, initPasswordOpt);
root.AddCommand(initCmd);

// ─── bmb join ──────────────────────────────────────────────────────────────

var joinRemoteOpt = new Option<string>("--remote", "Remote node URL (e.g. https://bmb.example.com)") { IsRequired = true };
var joinPasswordOpt = new Option<string>("--password", "Network master password") { IsRequired = true };
var joinNameOpt = new Option<string>("--name", "Name of this node") { IsRequired = true };
var joinAllowInsecureOpt = new Option<bool>("--allow-insecure-http",
    "Allow joining over plain HTTP to non-loopback hosts. Off by default — plain HTTP " +
    "lets a network attacker MITM the join and inject peer pubkeys. Use only on trusted LAN.");

var joinCmd = new Command("join", "Join a node to an existing BeeMemoryBank network");
joinCmd.AddOption(joinRemoteOpt);
joinCmd.AddOption(joinPasswordOpt);
joinCmd.AddOption(joinNameOpt);
joinCmd.AddOption(joinAllowInsecureOpt);
joinCmd.SetHandler(async (data, remote, password, name, allowInsecure) =>
{
    Environment.Exit(await JoinCommand.HandleAsync(data, remote, password, name, allowInsecure));
}, dataOption, joinRemoteOpt, joinPasswordOpt, joinNameOpt, joinAllowInsecureOpt);
root.AddCommand(joinCmd);

// ─── bmb status ─────────────────────────────────────────────────────────────

var statusCmd = new Command("status", "Node status: name, article count");
statusCmd.SetHandler(async (data) =>
{
    Environment.Exit(await StatusCommand.HandleAsync(data));
}, dataOption);
root.AddCommand(statusCmd);

// ─── bmb unlock ─────────────────────────────────────────────────────────────

var unlockPasswordOpt = new Option<string>("--password", "Master password") { IsRequired = true };

var unlockCmd = new Command("unlock", "Verify password (unlock master key)");
unlockCmd.AddOption(unlockPasswordOpt);
unlockCmd.SetHandler(async (data, password) =>
{
    Environment.Exit(await UnlockCommand.HandleAsync(data, password));
}, dataOption, unlockPasswordOpt);
root.AddCommand(unlockCmd);

// ─── bmb article ────────────────────────────────────────────────────────────

var articleCmd = new Command("article", "Article management");

// bmb article list
var listPathOpt = new Option<string?>("--path", "Filter by tree path (e.g. /Work)");
var listCmd = new Command("list", "List articles");
listCmd.AddOption(listPathOpt);
listCmd.SetHandler(async (data, path) =>
{
    Environment.Exit(await ArticleCommand.HandleListAsync(data, path));
}, dataOption, listPathOpt);
articleCmd.AddCommand(listCmd);

// bmb article get <id>
var getIdArg = new Argument<Guid>("id", "Article ID");
var getContentOpt = new Option<bool>("--content", "Show content (requires --password)");
var getPasswordOpt = new Option<string?>("--password", "Master password (for content decryption)");
var getCmd = new Command("get", "Article metadata (with --content — decrypt body)");
getCmd.AddArgument(getIdArg);
getCmd.AddOption(getContentOpt);
getCmd.AddOption(getPasswordOpt);
getCmd.SetHandler(async (data, id, showContent, password) =>
{
    Environment.Exit(await ArticleCommand.HandleGetAsync(data, id, showContent, password));
}, dataOption, getIdArg, getContentOpt, getPasswordOpt);
articleCmd.AddCommand(getCmd);

// bmb article create
var createTitleOpt = new Option<string>("--title", "Title") { IsRequired = true };
var createPathOpt = new Option<string>("--path", "Tree path (e.g. /Work/Dev)") { IsRequired = true };
var createContentOpt = new Option<string>("--content", "Article content") { IsRequired = true };
var createPasswordOpt = new Option<string>("--password", "Master password") { IsRequired = true };
var createCmd = new Command("create", "Create an article");
createCmd.AddOption(createTitleOpt);
createCmd.AddOption(createPathOpt);
createCmd.AddOption(createContentOpt);
createCmd.AddOption(createPasswordOpt);
createCmd.SetHandler(async (data, title, path, content, password) =>
{
    Environment.Exit(await ArticleCommand.HandleCreateAsync(data, title, path, content, password));
}, dataOption, createTitleOpt, createPathOpt, createContentOpt, createPasswordOpt);
articleCmd.AddCommand(createCmd);

// bmb article delete <id>
var deleteIdArg = new Argument<Guid>("id", "Article ID");
var deletePasswordOpt = new Option<string>("--password", "Master password (required to sign delete event)") { IsRequired = true };
var deleteCmd = new Command("delete", "Delete an article (soft delete)");
deleteCmd.AddArgument(deleteIdArg);
deleteCmd.AddOption(deletePasswordOpt);
deleteCmd.SetHandler(async (data, id, password) =>
{
    Environment.Exit(await ArticleCommand.HandleDeleteAsync(data, id, password));
}, dataOption, deleteIdArg, deletePasswordOpt);
articleCmd.AddCommand(deleteCmd);

root.AddCommand(articleCmd);

// ─── bmb agent ──────────────────────────────────────────────────────────────

var agentCmd = new Command("agent", "Agent management");

// bmb agent create
var agentNameOpt = new Option<string>("--name", "Agent name") { IsRequired = true };
var agentDescOpt = new Option<string?>("--description", "Optional description");
var agentOwnerOpt = new Option<int>("--owner-id", () => 0, "Owner user id (defaults to first active user)");
var agentPasswordOpt = new Option<string>("--password", "Master password") { IsRequired = true };

var agentCreateCmd = new Command("create", "Create an agent and print its API key");
agentCreateCmd.AddOption(agentNameOpt);
agentCreateCmd.AddOption(agentDescOpt);
agentCreateCmd.AddOption(agentOwnerOpt);
agentCreateCmd.AddOption(agentPasswordOpt);
agentCreateCmd.SetHandler(async (data, name, desc, ownerId, password) =>
{
    Environment.Exit(await AgentCommand.HandleCreateAsync(data, name, desc, ownerId, password));
}, dataOption, agentNameOpt, agentDescOpt, agentOwnerOpt, agentPasswordOpt);
agentCmd.AddCommand(agentCreateCmd);

root.AddCommand(agentCmd);

// ─── bmb snapshot ───────────────────────────────────────────────────────────

var snapshotCmd = new Command("snapshot", "Snapshot management (Phase 6)");

var snapshotCreateCmd = new Command("create", "Create a snapshot");
snapshotCreateCmd.SetHandler(async (data) =>
{
    Environment.Exit(await SnapshotCommand.HandleCreateAsync(data));
}, dataOption);
snapshotCmd.AddCommand(snapshotCreateCmd);

var snapshotListCmd = new Command("list", "List snapshots");
snapshotListCmd.SetHandler(async (data) =>
{
    Environment.Exit(await SnapshotCommand.HandleListAsync(data));
}, dataOption);
snapshotCmd.AddCommand(snapshotListCmd);

var fileIdArg = new Argument<string>("file-id-or-name", "File name or ID");
var snapshotRestoreNetworkCmd = new Command("restore-network", "Restore a snapshot across the network");
snapshotRestoreNetworkCmd.AddArgument(fileIdArg);
snapshotRestoreNetworkCmd.SetHandler(async (data, fileId) =>
{
    Environment.Exit(await SnapshotCommand.HandleRestoreNetworkAsync(data, fileId));
}, dataOption, fileIdArg);
snapshotCmd.AddCommand(snapshotRestoreNetworkCmd);

var fileArg = new Argument<string>("file-name", "Path or name of snapshot file");
var snapshotRestoreStandaloneCmd = new Command("restore-standalone", "Restore a snapshot locally and wipe network identity");
snapshotRestoreStandaloneCmd.AddArgument(fileArg);
snapshotRestoreStandaloneCmd.SetHandler(async (data, fileName) =>
{
    Environment.Exit(await SnapshotCommand.HandleRestoreStandaloneAsync(data, fileName));
}, dataOption, fileArg);
snapshotCmd.AddCommand(snapshotRestoreStandaloneCmd);

root.AddCommand(snapshotCmd);

// ─── bmb dek-rotate ─────────────────────────────────────────────────────────

var dekRotateCmd = new Command("dek-rotate", "DEK rotation management (Phase B6)");

var dekProposeCmd = new Command("propose", "Propose a DEK rotation");
dekProposeCmd.SetHandler(async () =>
{
    Environment.Exit(await DekRotateCommand.HandleProposeAsync());
});
dekRotateCmd.AddCommand(dekProposeCmd);

var dekAcceptEventIdArg = new Argument<string>("commit-event-id", "Commit event ID from propose");
var dekAcceptCmd = new Command("accept", "Accept and execute a proposed DEK rotation");
dekAcceptCmd.AddArgument(dekAcceptEventIdArg);
dekAcceptCmd.SetHandler(async (eventId) =>
{
    Environment.Exit(await DekRotateCommand.HandleAcceptAsync(eventId));
}, dekAcceptEventIdArg);
dekRotateCmd.AddCommand(dekAcceptCmd);

var dekProgressCmd = new Command("progress", "Show current DEK rotation progress");
dekProgressCmd.SetHandler(async () =>
{
    Environment.Exit(await DekRotateCommand.HandleProgressAsync());
});
dekRotateCmd.AddCommand(dekProgressCmd);

var dekCancelEventIdArg = new Argument<string>("event-id", "Rotation event ID to cancel");
var dekCancelCmd = new Command("cancel", "Cancel a proposed DEK rotation");
dekCancelCmd.AddArgument(dekCancelEventIdArg);
dekCancelCmd.SetHandler(async (eventId) =>
{
    Environment.Exit(await DekRotateCommand.HandleCancelAsync(eventId));
}, dekCancelEventIdArg);
dekRotateCmd.AddCommand(dekCancelCmd);

root.AddCommand(dekRotateCmd);

// ─── bmb restore ────────────────────────────────────────────────────────────

var restoreCmd = new Command("restore", "Restore process management");

var restoreStatusCmd = new Command("status", "Current restore state");
restoreStatusCmd.SetHandler(async (data) =>
{
    Environment.Exit(await SnapshotCommand.HandleRestoreStatusAsync(data));
}, dataOption);
restoreCmd.AddCommand(restoreStatusCmd);

root.AddCommand(restoreCmd);

// ────────────────────────────────────────────────────────────────────────────

return await root.InvokeAsync(args);
