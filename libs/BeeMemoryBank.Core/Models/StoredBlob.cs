namespace BeeMemoryBank.Core.Models;

/// <summary>One row of tbl_blob: ciphertext bytes and the lowercase-hex SHA-256 they are stored under.</summary>
public sealed record StoredBlob(string Hash, byte[] Data);
