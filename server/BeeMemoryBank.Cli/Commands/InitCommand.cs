using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class InitCommand
{
    /// <summary>
    /// Initializes the node: creates DB, generates keys, saves the password slot.
    /// Returns 0 on success, 1 on error.
    /// </summary>
    public static async Task<int> HandleAsync(
        string dataPath,
        string name,
        string password,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var initSvc = scope.ServiceProvider.GetRequiredService<InitializationService>();

        if (await initSvc.IsInitializedAsync())
        {
            await output.WriteLineAsync("Error: node is already initialized.");
            return 1;
        }

        await initSvc.InitializeAsync(name, password);
        await output.WriteLineAsync($"Node '{name}' successfully initialized in {dataPath}");
        return 0;
    }
}
