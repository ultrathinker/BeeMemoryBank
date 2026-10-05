namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>
/// The layout of a secret kept by the Android Keystore adapter: IV (12 bytes) followed by the AES-GCM ciphertext, whose last 16 bytes are the
/// authentication tag. A file that is shorter than that (cut off by a crash, damaged, replaced) is reported as an error the screen can name,
/// not sliced into an <see cref="ArgumentOutOfRangeException"/> or an <see cref="IndexOutOfRangeException"/>.
/// </summary>
public static class BlindKeystoreBlobLayout
{
    public const int IvLength = 12;
    public const int TagLength = 16;

    /// <summary>Joins the IV and the ciphertext into the file's content.</summary>
    public static byte[] Join(byte[] iv, byte[] ciphertext)
    {
        if (iv.Length != IvLength) throw new ArgumentException($"The IV must be {IvLength} bytes.", nameof(iv));
        var blob = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, iv.Length);
        return blob;
    }

    /// <summary>Splits a file's content; throws <see cref="InvalidDataException"/> when it cannot hold an IV and a tag.</summary>
    public static (byte[] Iv, int CipherOffset, int CipherLength) Split(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < IvLength + TagLength)
            throw new InvalidDataException($"The stored secret is damaged: {blob.Length} bytes, at least {IvLength + TagLength} are needed.");
        return (blob[..IvLength], IvLength, blob.Length - IvLength);
    }
}
