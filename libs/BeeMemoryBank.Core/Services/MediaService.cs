using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Core.Services;

public record MediaStorageOptions(string MediaDir);

public class MediaService(
    IMediaRepository mediaRepo,
    IArticleRepository articleRepo,
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
    private const long MaxInputSize = 50 * 1024 * 1024;
    private const long MaxFileSize = 20 * 1024 * 1024;
    private const int MaxImageDimension = 4096;
    private const int JpegQuality = 90;
    private const int JpegQualityDownscale = 85;

    public async Task<Media> CreateAsync(string fileName, string contentType, byte[] plaintext, Guid? articleId)
    {
        if (plaintext.Length > MaxInputSize)
            throw new ArgumentException($"Input size exceeds {MaxInputSize / (1024 * 1024)} MB limit.");
        if (!AllowedContentTypes.Contains(contentType))
            throw new ArgumentException($"Content type '{contentType}' is not allowed.");

        // Convert raster images to JPEG (except SVG and animated GIF). Downscale if still oversized.
        if (contentType != "image/svg+xml" && !IsAnimatedGif(plaintext, contentType))
        {
            var (jpegBytes, converted) = ConvertToJpeg(plaintext, contentType);
            if (converted)
            {
                plaintext = jpegBytes;
                contentType = "image/jpeg";
                fileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";
            }

            if (plaintext.Length > MaxFileSize)
            {
                plaintext = DownscaleJpeg(plaintext);
                contentType = "image/jpeg";
                fileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";
            }
        }

        if (plaintext.Length > MaxFileSize)
            throw new ArgumentException($"File size exceeds {MaxFileSize / (1024 * 1024)} MB limit.");

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            safeFileName = "image";

        var masterDek = session.GetMasterDek();
        Guid mediaId = Guid.NewGuid();
        byte[] ciphertext, iv, encryptedDek, dekIv;
        try
        {
            var mediaDek = DekManager.GenerateArticleDek();
            try
            {
                var dekAad = "bmb-media-dek"u8.ToArray().Concat(mediaId.ToByteArray()).ToArray();
                var bodyAad = "bmb-media"u8.ToArray().Concat(mediaId.ToByteArray()).ToArray();
                (ciphertext, iv) = MediaEncryptor.Encrypt(plaintext, mediaDek, bodyAad);
                (encryptedDek, dekIv) = DekManager.WrapDek(mediaDek, masterDek, dekAad);
            }
            finally
            {
                Array.Clear(mediaDek);
            }
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
            Id = mediaId,
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

    public async Task<(byte[] data, string contentType, string fileName)?> GetContentAsync(Guid id)
    {
        var media = await mediaRepo.GetByIdAsync(id);
        if (media == null)
            return null;

        if (media.ArticleId.HasValue)
        {
            // The repository's GetByIdAsync has a built-in ACL check via CallerScopeHolder.
            // If it returns null, the user doesn't have access to the article, so we deny media access.
            var article = await articleRepo.GetByIdAsync(media.ArticleId.Value);
            if (article == null)
                return null;
        }

        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(options.MediaDir, $"{id}.enc"));

        try
        {
            var isV1 = media.EncryptedDek.Length > 48 && media.EncryptedDek[0] == 0x01;
            var dekAad = isV1 ? "bmb-media-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;

            var mediaDek = session.TryUnwrapWithCandidates(masterDek =>
                DekManager.UnwrapDek(media.EncryptedDek, media.DekIV, masterDek, dekAad));
            try
            {
                var bodyAad = isV1 ? "bmb-media"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
                var plaintext = MediaEncryptor.Decrypt(ciphertext, media.IV, mediaDek, bodyAad);
                return (plaintext, media.ContentType, media.FileName);
            }
            finally
            {
                Array.Clear(mediaDek);
            }
        }
        catch
        {
            // Decryption failed. Don't leak details.
            return null;
        }
    }

    public Task SoftDeleteByArticleIdAsync(Guid articleId) => mediaRepo.SoftDeleteByArticleIdAsync(articleId);

    public async Task DeleteAsync(Guid id)
    {
        var media = await mediaRepo.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Media {id} not found.");

        if (media.ArticleId.HasValue)
        {
            var article = await articleRepo.GetByIdAsync(media.ArticleId.Value);
            if (article == null)
                throw new UnauthorizedAccessException($"Write access denied for media {id} linked to an inaccessible article.");
        }

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
        using var image = Image.Load(input);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
        var result = ms.ToArray();

        if (contentType == "image/jpeg" && result.Length >= input.Length)
            return (input, false);

        return (result, true);
    }

    private static byte[] DownscaleJpeg(byte[] input)
    {
        using var image = Image.Load(input);
        if (image.Width > MaxImageDimension || image.Height > MaxImageDimension)
        {
            var scale = Math.Min(
                (double)MaxImageDimension / image.Width,
                (double)MaxImageDimension / image.Height);
            var newWidth = (int)Math.Round(image.Width * scale);
            var newHeight = (int)Math.Round(image.Height * scale);
            image.Mutate(ctx => ctx.Resize(newWidth, newHeight));
        }
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQualityDownscale });
        return ms.ToArray();
    }
}
