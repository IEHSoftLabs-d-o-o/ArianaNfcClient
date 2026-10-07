using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ArianaNfcClient.Models;

namespace ArianaNfcClient.Views;

public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is AppStatusKind status ? status : AppStatusKind.Waiting;
        var color = kind switch
        {
            AppStatusKind.Success => Color.FromRgb(0x28, 0xA7, 0x45),
            AppStatusKind.Error or AppStatusKind.NoReader => Color.FromRgb(0xDC, 0x35, 0x45),
            AppStatusKind.AlreadyExists or AppStatusKind.RemoveTag => Color.FromRgb(0xFD, 0x7E, 0x14),
            _ => Color.FromRgb(0x2A, 0x3E, 0x91)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
