using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class UnlockCommand
{
    /// <summary>
    /// Verifies that the password correctly unlocks the master key.
    /// In CLI context, each command opens a session independently;
    /// this command is used for explicit password verification.
    /// Returns 0 on success, 1 on invalid password.
    /// </summary>
    public static async Task<int> HandleAsync(string dataPath, string password, TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();

        var unlocked = await session.UnlockAsync(password);
        if (!unlocked)
        {
            await output.WriteLineAsync("Error: invalid password.");
            return 1;
        }

        await output.WriteLineAsync("Password accepted. Session unlocked.");
        return 0;
    }
}
