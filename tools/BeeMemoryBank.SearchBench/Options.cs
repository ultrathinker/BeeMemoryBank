namespace BeeMemoryBank.SearchBench;

/// <summary>Parsed command-line options. Defaults match the brief's recommended values.</summary>
internal sealed class Options
{
    public string? DataPath { get; set; }
    public int? SeedArticles { get; set; }
    public int? SeedFolders { get; set; }
    public string Password { get; set; } = "test1234";
    public int Seed { get; set; } = 42;
    public string Locale { get; set; } = "ru,en";
    public string Scenarios { get; set; } = "title,content,semantic,mixed";
    public int Warmup { get; set; } = 3;
    public int Runs { get; set; } = 20;
    public int MixedDurationSeconds { get; set; } = 30;
    public int MixedClients { get; set; } = 20;
    public int SemanticWaitSeconds { get; set; } = 180;
    public string? OutputDir { get; set; }
    public string? Label { get; set; }
    public string? CorpusSizeLabel { get; set; }
    public bool AllowDataPath { get; set; }
    public bool KeepData { get; set; }
    public bool NoBuild { get; set; }
    public bool ForceSeed { get; set; }

    public HashSet<string> ScenarioSet => Scenarios
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.ToLowerInvariant())
        .ToHashSet();
}

internal static class OptionsParser
{
    public static (Options opt, string? error) Parse(string[] args)
    {
        var opt = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Val(string flag)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {flag}");
                return args[++i];
            }
            int IntVal(string flag)
            {
                var v = Val(flag);
                if (!int.TryParse(v, out var n)) throw new ArgumentException($"{flag} expects an integer, got '{v}'");
                return n;
            }
            switch (a)
            {
                case "--data-path": opt.DataPath = Val(a); break;
                case "--seed-articles": opt.SeedArticles = IntVal(a); break;
                case "--seed-folders": opt.SeedFolders = IntVal(a); break;
                case "--password": opt.Password = Val(a); break;
                case "--seed": opt.Seed = IntVal(a); break;
                case "--locale": opt.Locale = Val(a); break;
                case "--scenarios": opt.Scenarios = Val(a); break;
                case "--warmup": opt.Warmup = IntVal(a); break;
                case "--runs": opt.Runs = IntVal(a); break;
                case "--mixed-duration": opt.MixedDurationSeconds = IntVal(a); break;
                case "--mixed-clients": opt.MixedClients = IntVal(a); break;
                case "--semantic-wait": opt.SemanticWaitSeconds = IntVal(a); break;
                case "--output-dir": opt.OutputDir = Val(a); break;
                case "--label": opt.Label = Val(a); break;
                case "--corpus-size": opt.CorpusSizeLabel = Val(a); break;
                case "--allow-data-path": opt.AllowDataPath = true; break;
                case "--keep-data": opt.KeepData = true; break;
                case "--no-build": opt.NoBuild = true; break;
                case "--force-seed": opt.ForceSeed = true; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return (opt, "__help__");
                default:
                    return (opt, $"Unknown argument: {a}");
            }
        }

        if (opt.SeedArticles is > 0 && opt.SeedFolders is null or <= 0)
            return (opt, "--seed-articles requires --seed-folders to also be set.");

        return (opt, null);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            bmb-searchbench — drives a real BeeMemoryBank.Api instance with search queries and
            captures latency/throughput baselines for the search-100k initiative.

            Usage:
              bmb-searchbench --data-path <dir> [options]
              bmb-searchbench --seed-articles <N> --seed-folders <M> [--data-path <dir>] [options]

            Data source:
              --data-path <dir>        Existing seeded dir, or target dir when seeding. If omitted
                                       with --seed-articles, a scratch dir under the system temp dir
                                       is created automatically. NEVER point this at a real vault.
              --seed-articles <N>      Seed N articles via bmb-seedgen before benchmarking (the
                                       target path must be empty/missing, unless --force-seed).
              --seed-folders <M>       Folder count for seeding (required with --seed-articles).
              --password <pw>          Vault/unlock password (default: test1234, the seed default).
              --seed <S>               Seed determinism value (default: 42).
              --locale ru,en           Comma list of locales (default: ru,en).
              --force-seed             Pass --force to bmb-seedgen (seed onto an existing vault).

            Benchmark:
              --scenarios <list>       Comma list: title,content,semantic,mixed (default: all).
              --warmup <n>             Unmeasured warmup requests per query (default: 3).
              --runs <n>               Measured requests per query for closed-loop scenarios (default: 20).
              --mixed-duration <sec>   Mixed-load wall-clock duration (default: 30).
              --mixed-clients <N>      Mixed-load concurrent clients (default: 20).
              --semantic-wait <sec>    Max wait for the embedding backfill to settle before the
                                       semantic scenario (default: 180).

            Output:
              --output-dir <dir>       Where JSON baseline files are written
                                       (default: <repo>/_docs/search-100k/baseline).
              --label <text>           Run label embedded in filenames + JSON (default: timestamp).
              --corpus-size <label>    Corpus-size label for filenames (default: seed count, or 'existing').

            Safety / lifecycle:
              --allow-data-path        Override the "path is not scratch-like" soft refusal. NEVER
                                       overrides the hard refusal for real install paths.
              --keep-data              Don't delete the scratch data dir at exit (default: delete
                                       only if the harness created it).
              --no-build               Don't auto-build Api/SeedGen if their binaries are missing.

            The harness ALWAYS refuses paths that look like a real BeeMemoryBank install (under
            %LOCALAPPDATA%\BeeMemoryBankData, the default vault dir, or a user Documents/Desktop/
            Downloads folder). See README.md for the full heuristic.

            Examples:
              bmb-searchbench --seed-articles 10000 --seed-folders 200
              bmb-searchbench --data-path C:\Temp\bmb-bench\corpus-100k --corpus-size 100000
            """);
    }
}
