using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Cli;

/// <summary>A refusal the operator is told in one sentence: printed without a stack trace, exit code 1.</summary>
public sealed class CliVaultException(string message) : Exception(message);

/// <summary>
/// What the command line does with a data folder whose database is encrypted (docs/design/step-b-spec.md 5.9): a verb that reads or writes the vault in
/// its own process asks for the master password (<c>--password-stdin</c>, <c>--password</c> where the verb has it, or a terminal prompt) and opens the
/// database with it; without a password it says so and does nothing. The database closes again when the command ends. A plain folder never comes here.
/// </summary>
public static class CliSealed
{
    public const string NeedsPassword =
        "This vault is encrypted. Give the master password (--password-stdin, or type it when asked), or use the running node.";

    private static string? _passwordOnCommandLine;
    private static bool _passwordFromStdin;

    /// <summary>Standard input, standard error and whether stdin is a pipe: replaced by the tests.</summary>
    public static TextReader Stdin { get; set; } = Console.In;
    public static TextWriter Error { get; set; } = Console.Error;
    public static Func<bool> StdinIsRedirected { get; set; } = () => Console.IsInputRedirected;

    /// <summary>
    /// Takes note of how the operator gave the password, before the parser runs: <c>--password &lt;value&gt;</c>, <c>--master-password &lt;value&gt;</c> or
    /// <c>--password-stdin</c>. Nothing else of the command line is read here.
    /// </summary>
    public static void Capture(string[] args)
    {
        _passwordOnCommandLine = null;
        _passwordFromStdin = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--password-stdin") _passwordFromStdin = true;
            else if ((a == "--password" || a == "--master-password") && i + 1 < args.Length) _passwordOnCommandLine = args[++i];
            else if (a.StartsWith("--password=", StringComparison.Ordinal)) _passwordOnCommandLine = a["--password=".Length..];
            else if (a.StartsWith("--master-password=", StringComparison.Ordinal)) _passwordOnCommandLine = a["--master-password=".Length..];
        }
    }

    /// <summary>For the tests: the password the next command is given.</summary>
    public static void Set(string? onCommandLine, bool fromStdin)
    {
        _passwordOnCommandLine = onCommandLine;
        _passwordFromStdin = fromStdin;
    }

    public static string? ResolvePassword() =>
        PasswordInput.Resolve(_passwordOnCommandLine, _passwordFromStdin, "Master password: ", Stdin, StdinIsRedirected(), Error);

    public static bool IsSealed(string dataPath) => VaultStoreFiles.DetectMode(dataPath) == VaultStoreMode.Sealed;

    /// <summary>
    /// The sealed half of the command line's container: the boot store and its pieces, the gate over the vault file, and the unlock material.
    /// The chat database is the node's own and is not opened here. Nothing runs open steps: the node that sealed the folder made its schema.
    /// </summary>
    public static IServiceCollection AddSealedCli(this IServiceCollection services, string dataPath)
    {
        services.AddBootStore(dataPath);
        services.AddSealedUnlockMaterial();
        services.AddSingleton(sp => new VaultGate(
            sp.GetRequiredService<BootStore>(),
            sp.GetRequiredService<IDatabaseKeyService>(),
            () => [VaultFile(sp, dataPath)],
            () => [],
            () => null,
            () => sp.GetService<IUnlockMaterialMirror>(),
            sp.GetService<ILogger<VaultGate>>(),
            VaultStoreMode.Sealed)
        {
            // The node erases a replaced plain file; this process only refuses to open over one (review round 1, H1).
            PlainLeftovers = () => SealSwapResolver.Leftovers(dataPath),
        });
        services.AddSingleton<IVaultGate>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<IVaultKeySource>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealedVaultKeys>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealedUnlockDiagnostics>(sp => sp.GetRequiredService<VaultGate>());
        return services;
    }

    /// <summary>
    /// The pieces that encrypt a node while it is being set up (<c>bmb init</c>, BMB-306): the boot store, a gate over the vault file that is still plain, and
    /// "Encrypt this node" with a host that has nothing to add (the command line has no chat database, no relay and no sync loop here).
    /// <see cref="FreshNodeSealing"/> runs them after the vault is written.
    /// </summary>
    public static IServiceCollection AddFreshSealCli(this IServiceCollection services, string dataPath)
    {
        services.AddBootStore(dataPath);
        services.AddSealedUnlockMaterial();
        services.AddSingleton(sp =>
        {
            var gate = new VaultGate(
                sp.GetRequiredService<BootStore>(),
                sp.GetRequiredService<IDatabaseKeyService>(),
                () => [VaultFile(sp, dataPath)],
                () => [],
                () => null,
                () => sp.GetService<IUnlockMaterialMirror>(),
                sp.GetService<ILogger<VaultGate>>(),
                VaultStoreMode.Plain);
            gate.PlainLeftovers = () => SealSwapResolver.Leftovers(dataPath).Count == 0 ? [] : sp.GetRequiredService<SealMigrationService>().ClearLeftovers();
            return gate;
        });
        services.AddSingleton<IVaultGate>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<IVaultKeySource>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealedVaultKeys>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealedUnlockDiagnostics>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealMigrationHost, CliSealMigrationHost>();
        services.AddSingleton(sp => new SealMigrationService(dataPath, sp.GetRequiredService<VaultGate>(), sp.GetRequiredService<SessionService>(),
            sp.GetRequiredService<IDatabaseKeyService>(), sp.GetRequiredService<BootStore>(), sp.GetRequiredService<IUnlockMaterialMirror>(),
            sp.GetRequiredService<ISealMigrationHost>(), () => [VaultFile(sp, dataPath)], sp.GetRequiredService<MaintenanceModeService>(),
            scheduler: null, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ILogger<SealMigrationService>>()));
        services.AddScoped<FreshNodeSealing>();
        // The seal intent of the setup and the join this process runs (see SealedVaultServices).
        services.AddSingleton<ISealIntents>(sp => new SealIntents(dataPath, () => SealIntents.HoldsNoNode(sp.GetRequiredService<DbConnectionFactory>())));
        return services;
    }

    private static SealedFile VaultFile(IServiceProvider sp, string dataPath)
    {
        var vault = sp.GetRequiredService<DbConnectionFactory>();
        return new SealedFile(DbPurposes.Vault, Path.Combine(dataPath, VaultStoreFiles.Database), vault, vault.CreateConnection);
    }

    /// <summary>Opens the encrypted database of this process with the operator's password. Throws <see cref="CliVaultException"/> without one or with a wrong one.</summary>
    public static async Task OpenAsync(IServiceProvider services)
    {
        var password = ResolvePassword() ?? throw new CliVaultException(NeedsPassword);
        var session = services.GetRequiredService<SessionService>();
        if (!await session.UnlockAsync(password))
            throw new CliVaultException("Error: invalid password.");
    }
}
