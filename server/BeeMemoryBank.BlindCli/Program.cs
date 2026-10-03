using System.CommandLine;
using BeeMemoryBank.Cli.Commands;

// `bmb` of a blind node's container. The only verb is `bmb blind ...`; the rest of the full CLI (init, join, unlock,
// article, snapshot, rekey, dek-rotate...) works on a vault a blind node does not have.
var defaultDataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bmb", "data");

var dataOption = new Option<string>(
    "--data",
    () => defaultDataPath,
    "Path to the data directory (or BMB_DATA_PATH)");
dataOption.AddAlias("-d");

var root = new RootCommand("BeeMemoryBank blind node");
root.AddGlobalOption(dataOption);

BlindCommand.AddTo(root, dataOption);

return await root.InvokeAsync(args);
