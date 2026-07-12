using BeeMemoryBank.Crypto;

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    PrintHelp();
    return 0;
}

string subcommand = args[0];

switch (subcommand)
{
    case "gen-key":
        return RunGenKey(args[1..]);
    case "sign":
        return RunSign(args[1..]);
    case "verify":
        return RunVerify(args[1..]);
    default:
        Console.Error.WriteLine($"Unknown subcommand: {subcommand}");
        Console.Error.WriteLine("Run 'bmb-release --help' for usage.");
        return 1;
}

// ── gen-key ──────────────────────────────────────────────────────────────────

static int RunGenKey(string[] args)
{
    string? outDir = null;
    bool force = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--out requires a value"); return 1; }
                outDir = args[++i];
                break;
            case "--force":
                force = true;
                break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
        }
    }

    if (string.IsNullOrEmpty(outDir))
    {
        Console.Error.WriteLine("--out <dir> is required");
        return 1;
    }

    Directory.CreateDirectory(outDir);

    string privatePath = Path.Combine(outDir, "release-private.key");
    string publicPath  = Path.Combine(outDir, "release-public.key");

    if (!force)
    {
        if (File.Exists(privatePath))
        {
            Console.Error.WriteLine($"Key file already exists: {privatePath}");
            Console.Error.WriteLine("Use --force to overwrite.");
            return 1;
        }
        if (File.Exists(publicPath))
        {
            Console.Error.WriteLine($"Key file already exists: {publicPath}");
            Console.Error.WriteLine("Use --force to overwrite.");
            return 1;
        }
    }

    var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();

    File.WriteAllText(privatePath, Convert.ToBase64String(privateKey));
    File.WriteAllText(publicPath,  Convert.ToBase64String(publicKey));

    Console.WriteLine($"Generated Ed25519 key pair:");
    Console.WriteLine($"  Private key : {privatePath}");
    Console.WriteLine($"  Public key  : {publicPath}");
    Console.WriteLine("Keep the private key offline and secret.");
    return 0;
}

// ── sign ──────────────────────────────────────────────────────────────────────

static int RunSign(string[] args)
{
    string? keyPath  = null;
    string? filePath = null;
    string? outPath  = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--key":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--key requires a value"); return 1; }
                keyPath = args[++i];
                break;
            case "--file":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--file requires a value"); return 1; }
                filePath = args[++i];
                break;
            case "--out":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--out requires a value"); return 1; }
                outPath = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
        }
    }

    if (string.IsNullOrEmpty(keyPath))  { Console.Error.WriteLine("--key is required");  return 1; }
    if (string.IsNullOrEmpty(filePath)) { Console.Error.WriteLine("--file is required"); return 1; }
    if (string.IsNullOrEmpty(outPath))  { Console.Error.WriteLine("--out is required");  return 1; }

    if (!File.Exists(keyPath))  { Console.Error.WriteLine($"Private key not found: {keyPath}");  return 1; }
    if (!File.Exists(filePath)) { Console.Error.WriteLine($"File to sign not found: {filePath}"); return 1; }

    byte[] privateKey;
    try
    {
        privateKey = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read private key: {ex.Message}");
        return 1;
    }

    byte[] data = File.ReadAllBytes(filePath);
    byte[] signature = Ed25519Signer.Sign(privateKey, data);

    // Ensure output directory exists
    string? outDir = Path.GetDirectoryName(outPath);
    if (!string.IsNullOrEmpty(outDir))
        Directory.CreateDirectory(outDir);

    File.WriteAllText(outPath, Convert.ToBase64String(signature));

    Console.WriteLine($"Signed successfully:");
    Console.WriteLine($"  File      : {filePath}");
    Console.WriteLine($"  Signature : {outPath}");
    return 0;
}

// ── verify ────────────────────────────────────────────────────────────────────

static int RunVerify(string[] args)
{
    string? pubkeyPath = null;
    string? filePath   = null;
    string? sigPath    = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pubkey":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--pubkey requires a value"); return 1; }
                pubkeyPath = args[++i];
                break;
            case "--file":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--file requires a value"); return 1; }
                filePath = args[++i];
                break;
            case "--sig":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--sig requires a value"); return 1; }
                sigPath = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
        }
    }

    if (string.IsNullOrEmpty(pubkeyPath)) { Console.Error.WriteLine("--pubkey is required"); return 1; }
    if (string.IsNullOrEmpty(filePath))   { Console.Error.WriteLine("--file is required");   return 1; }
    if (string.IsNullOrEmpty(sigPath))    { Console.Error.WriteLine("--sig is required");     return 1; }

    if (!File.Exists(pubkeyPath)) { Console.Error.WriteLine($"Public key not found: {pubkeyPath}"); return 1; }
    if (!File.Exists(filePath))   { Console.Error.WriteLine($"File not found: {filePath}");         return 1; }
    if (!File.Exists(sigPath))    { Console.Error.WriteLine($"Signature not found: {sigPath}");      return 1; }

    byte[] publicKey;
    try
    {
        publicKey = Convert.FromBase64String(File.ReadAllText(pubkeyPath).Trim());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read public key: {ex.Message}");
        return 1;
    }

    byte[] signature;
    try
    {
        signature = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read signature: {ex.Message}");
        return 1;
    }

    byte[] data = File.ReadAllBytes(filePath);

    bool valid = Ed25519Signer.Verify(publicKey, data, signature);

    if (valid)
    {
        Console.WriteLine("Signature VALID — file is authentic and unmodified.");
        return 0;
    }
    else
    {
        Console.Error.WriteLine("Signature INVALID — file may have been tampered with or signed by a different key.");
        return 2;
    }
}

// ── help ──────────────────────────────────────────────────────────────────────

static void PrintHelp()
{
    Console.WriteLine("""
        bmb-release — BeeMemoryBank release signing tool

        Usage:
          bmb-release <subcommand> [options]

        Subcommands:
          gen-key   Generate a new Ed25519 keypair for release signing
          sign      Sign a file (e.g. releases.json) with the private key
          verify    Verify a file's signature with the public key

        gen-key options:
          --out <dir>     Directory to write key files to (required)
          --force         Overwrite existing key files (default: refuse)

        sign options:
          --key <path>    Path to private key file (required)
          --file <path>   Path to file to sign (required)
          --out <path>    Output path for the .sig file (required)

        verify options:
          --pubkey <path> Path to public key file (required)
          --file <path>   Path to file to verify (required)
          --sig <path>    Path to signature file (required)

        Examples:
          bmb-release gen-key --out ./keys
          bmb-release sign --key ./keys/release-private.key --file releases.json --out releases.json.sig
          bmb-release verify --pubkey ./keys/release-public.key --file releases.json --sig releases.json.sig

        Exit codes:
          0   Success (or valid signature for 'verify')
          1   Usage/input error
          2   Invalid signature ('verify' only)
        """);
}
