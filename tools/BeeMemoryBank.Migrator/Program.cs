using BeeMemoryBank.Migrator;

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    PrintHelp();
    return 0;
}

string? v1DbPath  = null;
string? v2DataPath = null;
string? password   = null;
string  nodeName   = "MigratedNode";
bool    dryRun     = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--v1-db":     v1DbPath   = args[++i]; break;
        case "--v2-data":   v2DataPath = args[++i]; break;
        case "--password":  password   = args[++i]; break;
        case "--node-name": nodeName   = args[++i]; break;
        case "--dry-run":   dryRun     = true;       break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (string.IsNullOrEmpty(v1DbPath))   { Console.Error.WriteLine("--v1-db is required");   return 1; }
if (string.IsNullOrEmpty(v2DataPath)) { Console.Error.WriteLine("--v2-data is required");  return 1; }
if (string.IsNullOrEmpty(password))   { Console.Error.WriteLine("--password is required"); return 1; }

if (!File.Exists(v1DbPath))
{
    Console.Error.WriteLine($"v1 database not found: {v1DbPath}");
    return 1;
}

Console.WriteLine($"Migration v1 → v2");
Console.WriteLine($"  Source : {v1DbPath}");
Console.WriteLine($"  Target : {v2DataPath}");
Console.WriteLine($"  DryRun : {dryRun}");
Console.WriteLine();

var opts = new MigrationOptions(v1DbPath, v2DataPath, password, nodeName, dryRun);
var svc  = new MigratorService(opts);

try
{
    var result = await svc.RunAsync();
    Console.WriteLine();
    Console.WriteLine($"Done: {result.Migrated} migrated, {result.Skipped} skipped, {result.Failed} failed.");
    return result.Failed > 0 ? 2 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        bmb-migrate — BeeMemoryBank v1 → v2 migration tool

        Usage:
          bmb-migrate --v1-db <path> --v2-data <dir> --password <pwd> [options]

        Required parameters:
          --v1-db <path>      Path to v1 SQLite database (beememorybank.db)
          --v2-data <dir>     v2 data directory (will be created if it does not exist)
          --password <pwd>    Password for the new (or existing) v2 node

        Options:
          --node-name <name>  Name of the new node (default: MigratedNode)
          --dry-run           Read v1 and show what would be migrated, without writing to v2
          --help              Show this help

        Example:
          bmb-migrate --v1-db ~/bee/data/beememorybank.db --v2-data ~/bee2/data --password MySecret
        """);
}
