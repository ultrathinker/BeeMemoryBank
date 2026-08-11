using BeeMemoryBank.SeedGen;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

string? dataPath = null;
string? password = null;
string localeRaw = "ru,en";
int? articles = null;
int? folders = null;
int seed = 42;
bool force = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-path": dataPath = NextValue(ref i, args[i]); break;
        case "--articles":  articles = Int(NextValue(ref i, args[i]), args[i]); break;
        case "--folders":   folders  = Int(NextValue(ref i, args[i]), args[i]); break;
        case "--seed":      seed     = Int(NextValue(ref i, args[i]), args[i]); break;
        case "--locale":    localeRaw = NextValue(ref i, args[i]); break;
        case "--password":  password = NextValue(ref i, args[i]); break;
        case "--force":     force = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (string.IsNullOrWhiteSpace(dataPath)) { Console.Error.WriteLine("--data-path is required"); return 1; }
if (articles is null || articles <= 0)   { Console.Error.WriteLine("--articles must be a positive integer"); return 1; }
if (folders is null || folders <= 0)     { Console.Error.WriteLine("--folders must be a positive integer"); return 1; }

var locales = ParseLocales(localeRaw);
if (locales.Count == 0)
    return 1;

password ??= "test1234";

var opts = new SeedOptions(dataPath, articles.Value, folders.Value, seed, locales, password, force);

try
{
    var runner = new SeedRunner(opts);
    return await runner.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    return 1;
}

string NextValue(ref int i, string flag)
{
    if (i + 1 >= args.Length)
        throw new ArgumentException($"Missing value for {flag}");
    return args[++i];
}

static int Int(string value, string flag)
{
    if (!int.TryParse(value, out var n))
        throw new ArgumentException($"{flag} expects an integer, got '{value}'");
    return n;
}

List<string> ParseLocales(string raw)
{
    var locales = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => SeedOptions.SupportedLocales.Contains(l, StringComparer.OrdinalIgnoreCase))
        .Select(l => l.ToLowerInvariant())
        .Distinct()
        .ToList();
    if (locales.Count == 0)
        Console.Error.WriteLine($"--locale '{raw}' has no supported values (supported: {string.Join(", ", SeedOptions.SupportedLocales)})");
    return locales;
}

static void PrintHelp()
{
    Console.WriteLine("""
        bmb-seedgen — seeds a BeeMemoryBank data directory with a synthetic corpus.

        Usage:
          bmb-seedgen --data-path <dir> --articles <N> --folders <M> [options]

        Required:
          --data-path <dir>     Target data directory (created if missing). Must NOT be a real vault.
          --articles <N>        Number of articles to generate.
          --folders <M>         Number of leaf folders to generate (depth 3–5).

        Options:
          --seed <S>            Determinism seed (default: 42).
          --locale ru,en        Comma list of locales, subset of: en, ru (default: ru,en).
          --password <pw>       Vault password (default: test1234).
          --force               Seed onto an already-initialised directory (otherwise refused).
          --help                Show this help.

        Determinism: the same --seed (with identical --articles/--folders/--locale) reproduces the
        same titles, tree, body text, and tags byte-for-byte across runs. Encryption-layer randomness
        (article IDs, IVs, protected-blob salts) is not part of that guarantee, only the content is.

        Examples:
          bmb-seedgen --data-path ./scratch/seed --articles 500 --folders 30
          bmb-seedgen --data-path ./scratch/big --articles 100000 --folders 2000 --seed 42
        """);
}
