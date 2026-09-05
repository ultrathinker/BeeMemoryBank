namespace BeeMemoryBank.Core.Models;

public class Media
{
    public Guid Id { get; set; }
    public Guid? ArticleId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    // "image" = inline, referenced in the article's markdown body. "attachment" = shown in a
    // separate list below the article, never inlined. Existing rows default to "image".
    public string Kind { get; set; } = "image";
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];
    public byte[] IV { get; set; } = [];
    // Content-address (lowercase hex SHA-256) of this media's ciphertext in tbl_blob. The read
    // path resolves the bytes from the blob store by this hash and falls back to the .enc file when
    // it is null (a row that predates the blob store, or whose create event was compacted away).
    // Item 16a, phase 1.
    public string? CiphertextSha256 { get; set; }
    public string Status { get; set; } = "A";
    public long LamportTs { get; set; }
    public Guid? SourceNodeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
