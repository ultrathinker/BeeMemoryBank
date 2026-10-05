using QRCoder;

namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>The QR picture of a pairing code: the same library and settings as the Android blind app's page.</summary>
public static class QrCodePng
{
    public static byte[] Create(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var generator = new QRCodeGenerator();
        return new PngByteQRCode(generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q)).GetGraphic(10);
    }
}
