using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// What <c>bmb rekey</c> needs from a sealed folder (docs/design/step-b-spec.md 13.2, 5.7): the owner's password opens the source temporarily through the
/// boot doors, and the run reads the source with its page keys. Nothing here takes the vault lease (the run takes the exclusive one) or unlocks a session.
/// For a plain folder it is empty and the run is the plain re-key.
/// </summary>
internal sealed class SealedRekeySource : IAsyncDisposable
{
    private ServiceProvider? _services;

    /// <summary>The options of a sealed run; null for a plain folder.</summary>
    public RekeySealedOptions? Options { get; private set; }

    /// <summary>Why the folder cannot be opened with this password; null when it can (or is plain).</summary>
    public string? Refusal { get; private set; }

    public static async Task<SealedRekeySource> OpenAsync(string dataPath, string password)
    {
        var source = new SealedRekeySource();
        var services = CliServiceProvider.CreateSealedForRekey(dataPath);
        if (services is null) return source;

        source._services = services;
        var session = services.GetRequiredService<SessionService>();
        var lease = await session.OpenForPasswordAsync(password);
        if (lease is null)
        {
            source.Refusal = "The password does not open this encrypted vault.";
            return source;
        }

        // The lease stays open until the provider goes: closing a database whose folder the run has just swapped would touch the wrong files.
        var boot = services.GetRequiredService<BootStore>();
        var generation = int.Parse(await boot.GetMetaAsync(BootMetaKeys.DbkGeneration) ?? "1", NumberStyles.None, CultureInfo.InvariantCulture);
        source.Options = new RekeySealedOptions
        {
            SourceKeys = services.GetRequiredService<IVaultKeySource>(),
            SourceDbkGeneration = generation,
            KeyService = services.GetRequiredService<IDatabaseKeyService>(),
            Mirror = services.GetRequiredService<IUnlockMaterialMirror>(),
        };
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
    }
}
