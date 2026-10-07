using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// <c>bmb user reset-password --user NAME [--recovery-key-stdin]</c>: the command-line twin of the Sign In
/// page's "Forgot your password? Use a recovery key". It proves the recovery key opens this node's master
/// key and sets a new password for that superadmin — the same <see cref="UserService.ResetPasswordWithRecoveryKeyAsync"/>
/// the web uses, so the same rules hold: superadmins only, one generic refusal, the session is never opened.
///
/// <para>Secrets never come from the command line. With <c>--recovery-key-stdin</c> the first line of stdin
/// is the recovery key and the second is the new password; without it both are asked for on a terminal,
/// with no echo (and the password twice). Anyone who can run this has the data directory in hand, so
/// there is no attempt throttle here — the key is 256 random bits, and the database file is a bigger
/// prize than a guessing game against it.</para>
/// </summary>
public static class UserCommand
{
    /// <summary>The one answer to every refusal; the same words as the web.</summary>
    internal const string Refused = "Error: the username or the recovery key is not correct.";

    public static async Task<int> HandleResetPasswordAsync(
        string dataPath,
        string userName,
        bool recoveryKeyStdin,
        TextReader? input = null,
        TextWriter? output = null)
    {
        output ??= Console.Out;

        string? recoveryKey;
        string? newPassword;
        if (recoveryKeyStdin)
        {
            input ??= Console.In;
            recoveryKey = input.ReadLine();
            newPassword = input.ReadLine();
            if (string.IsNullOrWhiteSpace(recoveryKey) || string.IsNullOrEmpty(newPassword))
            {
                await output.WriteLineAsync(
                    "Error: with --recovery-key-stdin, line 1 of stdin is the recovery key and line 2 is the new password.");
                return 1;
            }
        }
        else
        {
            if (input == null && Console.IsInputRedirected)
            {
                await output.WriteLineAsync(
                    "Error: stdin is not a terminal. Pipe the recovery key and the new password with --recovery-key-stdin, or run this in a terminal.");
                return 1;
            }
            recoveryKey = PromptNoEcho("Recovery key: ", input);
            newPassword = PromptNoEcho("New password: ", input);
            var repeat = PromptNoEcho("Repeat the new password: ", input);
            if (string.IsNullOrWhiteSpace(recoveryKey) || string.IsNullOrEmpty(newPassword))
            {
                await output.WriteLineAsync("Error: the recovery key and the new password are required.");
                return 1;
            }
            if (!string.Equals(newPassword, repeat, StringComparison.Ordinal))
            {
                await output.WriteLineAsync("Error: the two passwords do not match.");
                return 1;
            }
        }

        // The password rules say nothing about the key or the account, so this message is safe to be specific.
        try { UserService.ValidatePassword(newPassword); }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

        RecoveryResetResult result;
        try
        {
            result = await users.ResetPasswordWithRecoveryKeyAsync(userName, recoveryKey, newPassword);
        }
        catch (System.Security.SecurityException)
        {
            // A recovery slot with tampered KDF parameters: refused like every other "no".
            result = new RecoveryResetResult(RecoveryResetOutcome.WrongKey, null);
        }

        if (!result.Succeeded)
        {
            await audit.LogAsync("user", result.UserId?.ToString() ?? "-", "user_password_recovery_refused", "cli",
                $"Password reset with a recovery key refused on the command line ({(result.Outcome == RecoveryResetOutcome.WrongKey ? "key" : "account")})");
            await output.WriteLineAsync(Refused);
            return 1;
        }

        await audit.LogAsync("user", result.UserId!.Value.ToString(), "user_password_recovery_reset", "cli",
            $"Password reset with a recovery key for user #{result.UserId} on the command line");

        // Same node-local bookkeeping as the Admin -> Security card. A peer announcement is signed under the
        // master key, which this process does not hold open, so the other nodes are not told from here.
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        await nodeRepo.ClearMasterPasswordNoticeAsync();
        await nodeRepo.SetMasterPasswordChangedLocallyAtAsync(DateTime.UtcNow);

        await output.WriteLineAsync("Password changed. Sign in with the new password; the vault stays as it was until then.");
        var peers = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        if (peers.Count > 0)
            await output.WriteLineAsync(
                $"Note: {peers.Count} other node(s) still accept the OLD password. Change it on each of them (Admin -> Security).");
        return 0;
    }

    private static string? PromptNoEcho(string prompt, TextReader? input)
    {
        Console.Error.Write(prompt);
        if (input != null) return input.ReadLine();

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return new string(chars.ToArray());
    }
}
