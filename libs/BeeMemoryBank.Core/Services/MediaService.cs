using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    MediaStorageOptions options,
    IDbConnectionFactory connFactory,
    // Optional and last so the many direct constructions in tests keep compiling; DI supplies the
    // real one. Only used to report a media row whose file is gone, which is not a normal state
    // and must not pass silently.
    ILogger<MediaService>? logger = null,
    // Item 16a: the read path resolves ciphertext from the content-addressed blob store when the
    // row carries its hash, and falls back to the .enc file otherwise. Optional and last for the
    // same test-construction reason; when null (older test setups) the blob path is simply skipped
    // and the file fallback carries every read, exactly as before this change.
    IBlobRepository? blobRepo = null)
{
    private readonly ILogger<MediaService> logger = logger ?? NullLogger<MediaService>.Instance;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml"
    };
    private const long MaxInputSize = 50 * 1024 * 1024;
    private const long MaxFileSize = 20 * 1024 * 1024;
    private const long MaxAttachmentFileSize = 20 * 1024 * 1024;
    private const int MaxImageDimension = 4096;
    private const int JpegQuality = 90;
    private const int JpegQualityDownscale = 85;

    /// <param name="isAttachment">False (default) = inline image: restricted to
    /// <see cref="AllowedContentTypes"/>, re-encoded/downscaled to fit <see cref="MaxFileSize"/>.
    /// True = generic file attachment shown below the article, never inlined: any content type,
    /// stored as-is (no image processing), capped at <see cref="MaxAttachmentFileSize"/>.</param>
    public async Task<Media> CreateAsync(string fileName, string contentType, byte[] plaintext, Guid? articleId, bool isAttachment = false)
    {
        if (plaintext.Length > MaxInputSize)
            throw new ArgumentException($"Input size exceeds {MaxInputSize / (1024 * 1024)} MB limit.");

        // A nonexistent (or ACL-hidden) articleId must be rejected here: MediaRepository's write
        // check skips its FK-existence lookup for superadmin scope, so without this the INSERT
        // below fails with a raw SQLite FK-constraint exception instead of a clean error.
        //
        // Protected (second-layer passphrase) articles keep their body opaque to everyone but a
        // human with the passphrase. Media is wrapped by the MASTER DEK, not the article's
        // passphrase, so anything attached here would be readable without it — silently
        // undermining that guarantee. Simplest correct answer for now: disallow entirely.
        if (articleId.HasValue)
        {
            var article = await articleRepo.GetByIdAsync(articleId.Value);
            if (article == null)
                throw new ArgumentException($"Article {articleId} not found.");
            if (article.Protected)
                throw new InvalidOperationException(
                    "This article is password-protected (second-layer encryption); it cannot have attached media.");
        }

        if (isAttachment)
        {
            if (plaintext.Length > MaxAttachmentFileSize)
                throw new ArgumentException($"File size exceeds {MaxAttachmentFileSize / (1024 * 1024)} MB limit.");
        }
        else
        {
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
        }

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            safeFileName = isAttachment ? "file" : "image";

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
            CreatedAt = now,
            Kind = isAttachment ? "attachment" : "image",
            // The bytes go into the content-addressed blob store below (via LogMediaCreateAsync →
            // EnsureBlobAsync), under exactly this hash. Recording it on the row lets the read path
            // resolve the blob directly instead of only from the .enc file. Same hash function
            // (BlobHash.Compute), so the row and the blob agree by construction. Item 16a.
            CiphertextSha256 = BlobHash.Compute(ciphertext)
        };

        // Media ciphertext lives in the content-addressed blob store ONLY (16b) — no .enc file is
        // written any more. The blob is stored in the SAME transaction as the media row and its
        // sync event (LogMediaCreateAsync → EnsureBlobAsync), so there is no second store to keep
        // consistent and no cross-store ordering to get wrong: a crash before the commit leaves
        // nothing behind, and a commit persists blob + row + event atomically — the same shape
        // EventApplier's article create/update uses (EventApplier.Article.cs, H5).
        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await mediaRepo.CreateAsync(media, tx);
                await eventLogger.LogMediaCreateAsync(media, ciphertext, tx);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back */ }
                throw;
            }
        }

        eventLogger.SignalSync();

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

        // Item 16a: resolve the ciphertext from the content-addressed blob store first, by the hash
        // recorded on the row, and fall back to the .enc file. The blob is the store the create path
        // and the sync pusher already fill; the file is the legacy home. Preferring the blob is what
        // lets a node serve media it received purely over sync (blob shipped ahead of the event) or
        // through a snapshot/join that carried the database but not the media directory — cases the
        // file-only path could only answer with a 404.
        byte[]? ciphertext = null;
        if (blobRepo != null && !string.IsNullOrEmpty(media.CiphertextSha256))
        {
            ciphertext = await blobRepo.GetAsync(media.CiphertextSha256);
        }

        if (ciphertext == null)
        {
            // A media row whose .enc file is gone must degrade to "not found" (the endpoint's 404),
            // not to a 500. The two are reachable states, both from outside this node: a snapshot
            // restore or a join that brought the database across without the media directory, and an
            // event applied while MediaStorageOptions was not configured, which writes the row and no
            // file. Reading it blind threw FileNotFoundException, which nothing catches and
            // ExceptionStatusMap does not recognise — every broken image on the page became a server
            // error in the log, hiding the one fact an operator needs, which media is missing.
            var filePath = Path.Combine(options.MediaDir, $"{id}.enc");
            if (!File.Exists(filePath))
            {
                this.logger.LogWarning(
                    "Media {MediaId} ({FileName}) has a row but neither a blob ({Hash}) nor a file at {Path} — " +
                    "serving 404. This node's media store is incomplete; the bytes exist only on a peer that " +
                    "still has them.", id, media.FileName, media.CiphertextSha256 ?? "(none)", filePath);
                return null;
            }

            try
            {
                ciphertext = await File.ReadAllBytesAsync(filePath);
            }
            catch (IOException ex)
            {
                // Deleted between the check and the read, or unreadable. Same answer, still logged:
                // an unreadable file is an operational fact, not a caller error.
                this.logger.LogWarning(ex, "Media {MediaId} could not be read from {Path}", id, filePath);
                return null;
            }
        }

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
