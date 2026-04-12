using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using SkiaSharp;

namespace BeeMemoryBank.Core.Services;

public record MediaStorageOptions(string MediaDir);

public class MediaService(
    IMediaRepository mediaRepo,
    SessionService session,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    IEventLogger eventLogger,
    MediaStorageOptions options)
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml"
    };
    private const long MaxFileSize = 5 * 1024 * 1024;
    private const int JpegQuality = 90;

    public async Task<Media> CreateAsync(string fileName, string contentType, byte[] plaintext, Guid? articleId)
    {
        if (plaintext.Length > MaxFileSize)
            throw new ArgumentException($"File size exceeds {MaxFileSize / (1024 * 1024)} MB limit.");
        if (!AllowedContentTypes.Contains(contentType))
            throw new ArgumentException($"Content type '{contentType}' is not allowed.");

        // Convert raster images to JPEG 90% (except SVG and animated GIF)
        if (contentType != "image/svg+xml" && !IsAnimatedGif(plaintext, contentType))
        {
            var (jpegBytes, converted) = ConvertToJpeg(plaintext, contentType);
            if (converted)
            {
                plaintext = jpegBytes;
                contentType = "image/jpeg";
                fileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";
            }
        }

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            safeFileName = "image";

        var masterDek = session.GetMasterDek();
        byte[] ciphertext, iv, encryptedDek, dekIv;
        try
        {
            var mediaDek = DekManager.GenerateArticleDek();
            (ciphertext, iv) = MediaEncryptor.Encrypt(plaintext, mediaDek);
            (encryptedDek, dekIv) = DekManager.WrapDek(mediaDek, masterDek);
            Array.Clear(mediaDek);
        }
        finally
        {
            Array.Clear(masterDek);
        }

        var lamportTs = clock.Tick();
        var identity = await nodeRepo.GetAsync();
        var now = DateTime.UtcNow;

        var media = new Media
        {
            Id = Guid.NewGuid(),
            ArticleId = articleId,
            FileName = safeFileName,
            ContentType = contentType,
            FileSize = plaintext.Length,
            EncryptedDek = encryptedDek,
            DekIV = dekIv,
            IV = iv,
            Status = "A",
            LamportTs = lamportTs,
            SourceNodeId = identity?.NodeId,
            CreatedAt = now
        };

        Directory.CreateDirectory(options.MediaDir);
        // DB first, then file — if file write fails we can detect the missing file.
        // The reverse (file first, DB fails) leaves orphaned .enc files with no cleanup path.
        await mediaRepo.CreateAsync(media);
        await File.WriteAllBytesAsync(Path.Combine(options.MediaDir, $"{media.Id}.enc"), ciphertext);
        await eventLogger.LogMediaCreateAsync(media, ciphertext);

        return media;
    }

    public async Task<(byte[] data, string contentType, string fileName)> GetContentAsync(Guid id)
    {
        var media = await mediaRepo.GetByIdAsync(id)
                    ?? throw new KeyNotFoundException($"Media {id} not found.");

        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(options.MediaDir, $"{id}.enc"));

        var masterDek = session.GetMasterDek();
        try
        {
            var mediaDek = DekManager.UnwrapDek(media.EncryptedDek, media.DekIV, masterDek);
            var plaintext = MediaEncryptor.Decrypt(ciphertext, media.IV, mediaDek);
            Array.Clear(mediaDek);
            return (plaintext, media.ContentType, media.FileName);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    public Task SoftDeleteByArticleIdAsync(Guid articleId) => mediaRepo.SoftDeleteByArticleIdAsync(articleId);

    public async Task DeleteAsync(Guid id)
    {
        var media = await mediaRepo.GetByIdAsync(id)
                    ?? throw new KeyNotFoundException($"Media {id} not found.");
        await mediaRepo.SoftDeleteAsync(id);
        await eventLogger.LogMediaDeleteAsync(id);
    }

    public Task<List<Media>> GetByArticleIdAsync(Guid articleId) => mediaRepo.GetByArticleIdAsync(articleId);

    private static bool IsAnimatedGif(byte[] data, string contentType)
    {
        if (contentType != "image/gif" || data.Length < 13) return false;
        // Count GIF image descriptors (0x2C byte after extension blocks)
        int frameCount = 0;
        for (int i = 13; i < data.Length - 1 && frameCount < 2; i++)
        {
            if (data[i] == 0x2C) frameCount++;
        }
        return frameCount > 1;
    }

    private static (byte[] data, bool converted) ConvertToJpeg(byte[] input, string contentType)
    {
        if (contentType == "image/jpeg")
        {
            // Already JPEG — re-encode at quality 90 only if it would save space
            using var bitmap = SKBitmap.Decode(input);
            if (bitmap == null) return (input, false);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            var result = encoded.ToArray();
            return result.Length < input.Length ? (result, true) : (input, false);
        }

        // PNG, WebP, non-animated GIF → JPEG
        using var bmp = SKBitmap.Decode(input);
        if (bmp == null) return (input, false);
        using var enc = bmp.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return (enc.ToArray(), true);
    }
}
