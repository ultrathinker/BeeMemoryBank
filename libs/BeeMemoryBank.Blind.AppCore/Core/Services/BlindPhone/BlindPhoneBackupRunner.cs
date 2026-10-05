using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>How a backup run ended.</summary>
public enum BlindBackupOutcomeKind { Done, Waiting, Paused, NotAvailable, Failed }

public sealed record BlindBackupOutcome(BlindBackupOutcomeKind Kind, string Message, string? FilePath = null);

/// <summary>
/// Makes the Android blind node's backup files (plan section 10): the current blind package, encrypted
/// with the backup key, behind the open recovery-set header (<see cref="AndroidBackupFile"/>).
///
/// <para>A run is resumable end to end: the package it backs up is written once next to the backup and
/// kept until the backup is finished, so an interrupted run continues the same file with the same source
/// instead of starting over.</para>
/// </summary>
public sealed class BlindPhoneBackupRunner(
    BlindPhoneState state,
    IBlindPhoneKeys keys,
    IBlindPackageSource package,
    IRecoverySetJsonSource recoverySet,
    BlindPhoneLog log,
    string backupsDirectory,
    TimeProvider time)
{
    /// <summary>How many finished backups stay on the phone; older ones are removed after a new one.</summary>
    public const int KeptBackups = 3;
    public const string Extension = ".bmbbackup";

    public string BackupsDirectory => backupsDirectory;

    /// <summary>Finished backups, newest first.</summary>
    /// <summary>True if the recovery set's sealed secrets include <paramref name="name"/> (any case).</summary>
    private static bool HoldsSealedSecret(string recoverySetJson, string name)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(recoverySetJson);
            return doc.RootElement.TryGetProperty("sealed_secrets", out var secrets)
                && secrets.ValueKind == System.Text.Json.JsonValueKind.Array
                && secrets.EnumerateArray().Any(s =>
                    s.ValueKind == System.Text.Json.JsonValueKind.Object
                    && s.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public IReadOnlyList<FileInfo> Backups() =>
        Directory.Exists(backupsDirectory)
            ? new DirectoryInfo(backupsDirectory).GetFiles("*" + Extension)
                // Not the .part / .source files of a run in progress.
                .Where(f => f.Name.EndsWith(Extension, StringComparison.Ordinal))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : [];

    public async Task<BlindBackupOutcome> RunAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (state.NodeId is not { } nodeId)
            return new(BlindBackupOutcomeKind.Failed, "This phone is not set up as a blind copy.");

        var backupKey = keys.LoadBackupKey();
        if (backupKey is null)
            return new(BlindBackupOutcomeKind.Failed, "This phone has no backup key. Disconnect and set it up again.");

        try
        {
            // A backup is worth writing only if a restore can open it: its recovery set must already
            // hold the backup key Windows sealed for this phone at pairing (it arrives by sync). Until
            // then, wait — never write a file that only this phone could read.
            var recoverySetJson = await recoverySet.BuildJsonAsync(ct);
            var keyName = $"android-backup:{nodeId}";
            if (!HoldsSealedSecret(recoverySetJson, keyName))
                return new(BlindBackupOutcomeKind.Waiting,
                    "Waiting for the computer's sealed copy of the backup key to arrive by sync.");

            Directory.CreateDirectory(backupsDirectory);
            var name = state.PendingBackupName ??= $"bmb-phone-{time.GetUtcNow():yyyyMMdd-HHmmss}{Extension}";
            var output = Path.Combine(backupsDirectory, name);
            var source = output + ".source";
            if (!File.Exists(source))
            {
                await package.CreateAsync(source + ".tmp", ct);
                File.Move(source + ".tmp", source, overwrite: true);
            }

            await AndroidBackupWriter.WriteAsync(source, output, backupKey, nodeId,
                keyName, recoverySetJson, progress: progress, ct: ct);

            // WriteAsync returns only after decrypting the finished file and matching it against the
            // package copy, so the copy can go now; on any failure it stays for the next attempt.
            File.Delete(source);
            state.PendingBackupName = null;
            state.LastBackupAt = time.GetUtcNow();
            foreach (var old in Backups().Skip(KeptBackups)) old.Delete();
            log.Add("backup", $"Backup made: {name} ({new FileInfo(output).Length / 1024} KiB).");
            return new(BlindBackupOutcomeKind.Done, "Backup made.", output);
        }
        catch (OperationCanceledException)
        {
            log.Add("backup", "Backup paused; it continues where it stopped.");
            return new(BlindBackupOutcomeKind.Paused, "Paused; continues where it stopped.");
        }
        catch (BlindFeaturePendingException ex)
        {
            return new(BlindBackupOutcomeKind.NotAvailable, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Add("backup", $"Backup failed: {ex.Message}");
            return new(BlindBackupOutcomeKind.Failed, ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(backupKey);
        }
    }
}
