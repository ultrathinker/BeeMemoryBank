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

var joinCmd = new Command("join", "Join a node to an existing BeeMemoryBank network");
joinCmd.AddOption(joinRemoteOpt);
joinCmd.AddOption(joinPasswordOpt);
joinCmd.AddOption(joinNameOpt);
joinCmd.SetHandler(async (data, remote, password, name) =>
{
    Environment.Exit(await JoinCommand.HandleAsync(data, remote, password, name));
}, dataOption, joinRemoteOpt, joinPasswordOpt, joinNameOpt);
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
var deleteCmd = new Command("delete", "Delete an article (soft delete)");
deleteCmd.AddArgument(deleteIdArg);
deleteCmd.SetHandler(async (data, id) =>
{
    Environment.Exit(await ArticleCommand.HandleDeleteAsync(data, id));
}, dataOption, deleteIdArg);
articleCmd.AddCommand(deleteCmd);

root.AddCommand(articleCmd);

// ─── bmb snapshot ───────────────────────────────────────────────────────────

var snapshotCmd = new Command("snapshot", "Snapshot management (Phase 2)");

var snapshotCreateCmd = new Command("create", "Create a snapshot");
snapshotCreateCmd.SetHandler(async (data) =>
{
    Environment.Exit(await SnapshotCommand.HandleCreateAsync(data));
}, dataOption);
snapshotCmd.AddCommand(snapshotCreateCmd);

var snapshotPathArg = new Argument<string>("path", "Path to snapshot file");
var snapshotRestoreCmd = new Command("restore", "Restore from a snapshot");
snapshotRestoreCmd.AddArgument(snapshotPathArg);
snapshotRestoreCmd.SetHandler(async (data, path) =>
{
    Environment.Exit(await SnapshotCommand.HandleRestoreAsync(data, path));
}, dataOption, snapshotPathArg);
snapshotCmd.AddCommand(snapshotRestoreCmd);

root.AddCommand(snapshotCmd);

// ────────────────────────────────────────────────────────────────────────────

return await root.InvokeAsync(args);
