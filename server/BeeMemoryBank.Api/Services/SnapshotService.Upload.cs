using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class SnapshotService
{
    public async Task<SnapshotUploadResponse> SaveUploadedAsync(Stream stream)
    {
        Directory.CreateDirectory(SnapshotsDir);
        var fileId = Guid.NewGuid();
        var tempFile = Path.Combine(SnapshotsDir, $".upload-{fileId:N}.tmp");
        var tempFileMoved = false;

        try
        {

        await using (var fs = File.Create(tempFile))
        {
            await stream.CopyToAsync(fs);
        }

        var hash = await ComputeHashAsync(tempFile);

        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-upload-{fileId:N}");
        string originatorNodeId = "unknown";
        string createdAt = DateTime.UtcNow.ToString("O");
        bool networkRestoreAllowed = true;
        string? dekMismatchReason = null;

        try
        {
            Directory.CreateDirectory(tempDir);

            // Pre-extract manifest and signature for verification BEFORE full extraction.
            // This prevents a malicious tar from extracting bombs before we validate provenance.
            byte[]? preManifestBytes = null;
            byte[]? preSigBytes = null;
            await using (var preFs = File.OpenRead(tempFile))
            await using (var preGz = new GZipStream(preFs, CompressionMode.Decompress))
            using (var preTar = new TarReader(preGz))
            {
                while (await preTar.GetNextEntryAsync() is { } entry)
                {
                    if (entry.EntryType != TarEntryType.RegularFile) continue;
                    if (entry.Name == ManifestFileName)
                    {
                        await using var ms = new MemoryStream();
                        await entry.DataStream!.CopyToAsync(ms);
                        preManifestBytes = ms.ToArray();
                    }
                    else if (entry.Name == ManifestFileName + ".sig")
                    {
                        await using var ms = new MemoryStream();
                        await entry.DataStream!.CopyToAsync(ms);
                        preSigBytes = ms.ToArray();
                    }
                }
            }

            if (preManifestBytes != null)
            {
                var manifest = JsonDocument.Parse(preManifestBytes);
                if (manifest.RootElement.TryGetProperty("producerNodeId", out var pid))
                    originatorNodeId = pid.GetString() ?? "unknown";
                if (manifest.RootElement.TryGetProperty("createdAt", out var ca))
                    createdAt = ca.GetString() ?? createdAt;

                if (originatorNodeId != "unknown")
                {
                    bool isSelf = false;
                    if (_nodeRepo != null)
                    {
                        var ident = await _nodeRepo.GetAsync();
                        if (ident != null && ident.NodeId.ToString() == originatorNodeId)
                            isSelf = true;
                    }

                    bool isTrusted = false;
                    BeeMemoryBank.Core.Models.WhitelistEntry? trustedEntry = null;
                    if (_whitelistRepo != null && Guid.TryParse(originatorNodeId, out var parsedOriginatorId))
                    {
                        trustedEntry = await _whitelistRepo.GetByNodeIdAsync(parsedOriginatorId);
                        if (trustedEntry != null) isTrusted = true;
                    }

                    if (!isSelf && !isTrusted)
                    {
                        networkRestoreAllowed = false;
                        dekMismatchReason = "Originator node not in whitelist (likely foreign network)";
                    }
                    else if (trustedEntry != null)
                    {
                        if (preSigBytes != null)
                        {
                            try
                            {
                                if (!Ed25519Signer.Verify(trustedEntry.Ed25519PublicKey, BuildSigPayloadEmbedded(preManifestBytes!), preSigBytes))
                                {
                                    // The archive *claims* to be from a peer we trust AND ships
                                    // a signature, but it doesn't verify against that peer's
                                    // public key. Either the file is tampered with or someone
                                    // forged the manifest's producerNodeId. Reject outright —
                                    // accepting it would let an attacker plant a fake snapshot
                                    // that the admin later restores in standalone mode (which
                                    // would adopt the claimed identity).
                                    _logger?.LogWarning("Rejected uploaded snapshot: manifest signature invalid for claimed originator {NodeId}.", originatorNodeId);
                                    throw new InvalidOperationException("Manifest signature is invalid for the claimed originator.");
                                }
                            }
                            catch (InvalidOperationException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Manifest signature read/verify error — rejecting upload.");
                                throw new InvalidOperationException("Manifest signature could not be read or verified.");
                            }
                        }
                        else
                        {
                            networkRestoreAllowed = false;
                            dekMismatchReason = "Snapshot is not signed (manifest.json.sig missing) — network-wide restore disabled";
                            _logger?.LogInformation("Uploaded snapshot from {NodeId} is unsigned; network-wide restore disabled.", originatorNodeId);
                        }
                    }
                }
                else
                {
                    networkRestoreAllowed = false;
                    dekMismatchReason = "Originator node not in whitelist (likely foreign network)";
                }
            }
            else
            {
                networkRestoreAllowed = false;
                dekMismatchReason = "Invalid snapshot format (missing manifest.json)";
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var shortOriginator = originatorNodeId == "unknown" ? "unknown" : (originatorNodeId.Length > 8 ? originatorNodeId[..8] : originatorNodeId);
        // Include the fileId at the end of the filename so FindSnapshotFileById can locate it via
        // exact suffix match (-<id:N>.bin) instead of fragile substring search across the dir.
        var fileName = $"imported-{shortOriginator}-{timestamp}-{fileId:N}.bin";
        var finalPath = Path.Combine(SnapshotsDir, fileName);
        
        File.Move(tempFile, finalPath, overwrite: true);
        tempFileMoved = true;
        var fi = new FileInfo(finalPath);

        return new SnapshotUploadResponse(
            FileId: fileId,
            FileName: fileName,
            FileSizeBytes: fi.Length,
            OriginatorNodeId: originatorNodeId,
            SnapshotHash: hash,
            CreatedAt: createdAt,
            NetworkRestoreAllowed: networkRestoreAllowed,
            DekMismatchReason: dekMismatchReason
        );
        }
        finally
        {
            // Defensive cleanup: if anything threw between File.Create(tempFile) and the final
            // File.Move (extract failure, manifest parse error, OOM during hash, etc), the
            // .upload-<guid>.tmp file would otherwise pile up in the snapshots dir indefinitely.
            if (!tempFileMoved && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* ignore */ }
            }
        }
    }
}
