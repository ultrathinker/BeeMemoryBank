namespace BeeMemoryBank.Crypto;

/// <summary>
/// The on-disk shape of one sealed per-entity row: the body ciphertext (AES-256-GCM under the
/// entity DEK) and the entity DEK wrapped under the master DEK. Field names match the
/// <c>ciphertext</c>/<c>iv</c>/<c>encrypted_dek</c>/<c>dek_iv</c> columns every such table uses.
/// </summary>
public readonly record struct SealedEnvelope(byte[] Ciphertext, byte[] Iv, byte[] WrappedDek, byte[] DekIv);

/// <summary>
/// The single definition of how an article or media row is framed: which AAD its wrapped DEK and
/// its body are sealed under, and how a reader tells the legacy v0 framing from v1.
///
/// <para>
/// Two framings exist and both must stay readable forever:
/// <list type="bullet">
///   <item><b>v0</b> (legacy): the wrapped DEK is exactly 48 bytes (no version byte), and neither
///   the DEK wrap nor the body was sealed with any AAD.</item>
///   <item><b>v1</b>: the wrapped DEK carries a leading <c>0x01</c> version byte, the DEK is sealed
///   under <c>dekPrefix || id</c> and the body under <c>bodyPrefix || id</c>.</item>
/// </list>
/// The framing is decided from the wrapped-DEK blob alone (<see cref="IsVersioned"/>), and that one
/// answer selects the AAD for BOTH the DEK unwrap and the body decrypt. The two must never be
/// decided separately: a row whose DEK framing is changed without re-sealing its body (for example,
/// re-wrapping a v0 DEK as v1) makes every reader apply v1 body AAD to a body sealed with none, and
/// the row can no longer be decrypted by anyone.
/// </para>
///
/// <para>
/// New rows are always written as v1. Only <see cref="RewrapDek(byte[], byte[], byte[], byte[])"/>
/// (DEK rotation, where the body is not touched) may produce a v0 row, and only from a v0 row.
/// </para>
/// </summary>
public sealed class EnvelopeFraming
{
    private const byte Version1 = 0x01;
    private const int LegacyWrappedDekSize = CryptoConstants.KeySize + CryptoConstants.TagSize;

    /// <summary>Article bodies, and every row that carries a copy of an article body's wrapped DEK
    /// (article versions, conflict versions). The id is always the ARTICLE id.</summary>
    public static readonly EnvelopeFraming Article = new("bmb-art-dek"u8.ToArray(), "bmb-art-body"u8.ToArray());

    /// <summary>Media (images and attachments). The id is the media id.</summary>
    public static readonly EnvelopeFraming Media = new("bmb-media-dek"u8.ToArray(), "bmb-media"u8.ToArray());

    private readonly byte[] _dekPrefix;
    private readonly byte[] _bodyPrefix;

    private EnvelopeFraming(byte[] dekPrefix, byte[] bodyPrefix)
    {
        _dekPrefix = dekPrefix;
        _bodyPrefix = bodyPrefix;
    }

    /// <summary>
    /// The v0/v1 dispatch rule: a wrapped DEK is v1 when it is longer than the 48-byte legacy form
    /// and starts with the <c>0x01</c> version byte. Malformed lengths are rejected later by
    /// <see cref="DekManager.UnwrapDek"/>; this only decides which AAD a reader supplies.
    /// </summary>
    public static bool IsVersioned(byte[] wrappedDek)
    {
        ArgumentNullException.ThrowIfNull(wrappedDek);
        return wrappedDek.Length > LegacyWrappedDekSize && wrappedDek[0] == Version1;
    }

    /// <summary>The v1 AAD for the wrapped entity DEK: <c>dekPrefix || id.ToByteArray()</c>.</summary>
    public byte[] DekAad(Guid id) => Concat(_dekPrefix, id);

    /// <summary>The v1 AAD for the body ciphertext: <c>bodyPrefix || id.ToByteArray()</c>.</summary>
    public byte[] BodyAad(Guid id) => Concat(_bodyPrefix, id);

    /// <summary>
    /// Unwraps an existing row's entity DEK with the AAD its framing requires. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/> when
    /// <paramref name="masterDek"/> is not the key the row was wrapped under, so it can be used
    /// directly as a candidate-DEK probe. Caller owns (and must clear) the returned DEK.
    /// </summary>
    public byte[] UnwrapDek(Guid id, byte[] wrappedDek, byte[] dekIv, byte[] masterDek)
        => DekManager.UnwrapDek(wrappedDek, dekIv, masterDek, IsVersioned(wrappedDek) ? DekAad(id) : null);

    /// <summary>
    /// Decrypts a row's body with an already-unwrapped entity DEK. <paramref name="wrappedDek"/> is
    /// the row's wrapped DEK blob — it decides the body's framing, exactly as it did for the unwrap.
    /// Caller owns (and must clear) the returned plaintext.
    /// </summary>
    public byte[] DecryptBody(Guid id, byte[] wrappedDek, byte[] entityDek, byte[] ciphertext, byte[] iv)
        => MediaEncryptor.Decrypt(ciphertext, iv, entityDek, IsVersioned(wrappedDek) ? BodyAad(id) : null);

    /// <summary><see cref="DecryptBody"/> for UTF-8 text bodies (articles).</summary>
    public string DecryptBodyText(Guid id, byte[] wrappedDek, byte[] entityDek, byte[] ciphertext, byte[] iv)
        => ArticleEncryptor.Decrypt(ciphertext, iv, entityDek, IsVersioned(wrappedDek) ? BodyAad(id) : null);

    /// <summary>
    /// Seals a body under an EXISTING entity DEK and wraps that DEK under
    /// <paramref name="masterDek"/>, always in the v1 framing. Safe for a row that was v0 before:
    /// the body and the DEK are both re-sealed together, so their framing stays in agreement.
    /// </summary>
    public SealedEnvelope Seal(Guid id, byte[] plaintext, byte[] entityDek, byte[] masterDek)
    {
        var (ciphertext, iv) = MediaEncryptor.Encrypt(plaintext, entityDek, BodyAad(id));
        var (wrappedDek, dekIv) = DekManager.WrapDek(entityDek, masterDek, DekAad(id));
        return new SealedEnvelope(ciphertext, iv, wrappedDek, dekIv);
    }

    /// <summary><see cref="Seal"/> for UTF-8 text bodies (articles).</summary>
    public SealedEnvelope SealText(Guid id, string plaintext, byte[] entityDek, byte[] masterDek)
    {
        var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, entityDek, BodyAad(id));
        var (wrappedDek, dekIv) = DekManager.WrapDek(entityDek, masterDek, DekAad(id));
        return new SealedEnvelope(ciphertext, iv, wrappedDek, dekIv);
    }

    /// <summary>Seals a new row: generates a fresh entity DEK, seals with <see cref="Seal"/>, and
    /// clears the DEK.</summary>
    public SealedEnvelope SealNew(Guid id, byte[] plaintext, byte[] masterDek)
    {
        var entityDek = DekManager.GenerateArticleDek();
        try
        {
            return Seal(id, plaintext, entityDek, masterDek);
        }
        finally
        {
            Array.Clear(entityDek);
        }
    }

    /// <summary><see cref="SealNew"/> for UTF-8 text bodies (articles).</summary>
    public SealedEnvelope SealNewText(Guid id, string plaintext, byte[] masterDek)
    {
        var entityDek = DekManager.GenerateArticleDek();
        try
        {
            return SealText(id, plaintext, entityDek, masterDek);
        }
        finally
        {
            Array.Clear(entityDek);
        }
    }

    /// <summary>
    /// Re-wraps an existing row's entity DEK under <paramref name="newMasterDek"/> without touching
    /// its body, preserving the row's framing. See
    /// <see cref="RewrapDek(byte[], byte[], byte[], byte[])"/>.
    /// </summary>
    public (byte[] wrapped, byte[] iv) RewrapDek(Guid id, byte[] entityDek, byte[] originalWrappedDek, byte[] newMasterDek)
        => RewrapDek(entityDek, originalWrappedDek, newMasterDek, DekAad(id));

    /// <summary>
    /// Re-wraps an existing row's entity DEK under <paramref name="newMasterDek"/> without touching
    /// its body. The framing of <paramref name="originalWrappedDek"/> is preserved: a v0 row stays
    /// v0 (<see cref="DekManager.WrapDekLegacyV0"/>), because its body was sealed without AAD and a
    /// v1 label would make every reader apply one; a v1 row stays v1 under
    /// <paramref name="v1DekAad"/> — the same AAD it was unwrapped with.
    /// </summary>
    public static (byte[] wrapped, byte[] iv) RewrapDek(byte[] entityDek, byte[] originalWrappedDek, byte[] newMasterDek, byte[]? v1DekAad)
        => IsVersioned(originalWrappedDek)
            ? DekManager.WrapDek(entityDek, newMasterDek, v1DekAad)
            : DekManager.WrapDekLegacyV0(entityDek, newMasterDek);

    private static byte[] Concat(byte[] prefix, Guid id)
    {
        var idBytes = id.ToByteArray();
        var aad = new byte[prefix.Length + idBytes.Length];
        prefix.CopyTo(aad, 0);
        idBytes.CopyTo(aad, prefix.Length);
        return aad;
    }
}
