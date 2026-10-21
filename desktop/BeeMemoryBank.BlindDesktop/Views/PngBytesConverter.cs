using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace BeeMemoryBank.BlindDesktop.Views;

/// <summary>
/// The QR picture of a pairing code: the view model holds the PNG bytes (it knows nothing of the UI toolkit), the image control shows this. A picture that cannot be made is not worth
/// the app: it is written to the error log and the image stays empty (the text of the code is on the screen as well).
/// </summary>
public sealed class PngBytesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] png) return null;
        try
        {
            return new Bitmap(new MemoryStream(png));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Showing the QR picture failed", ex);
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
