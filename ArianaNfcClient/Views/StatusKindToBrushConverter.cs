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
            AppStatusKind.Success => Color.FromRgb(0x2F, 0x8A, 0x5F),
            AppStatusKind.Error => Color.FromRgb(0xB8, 0x4A, 0x4A),
            AppStatusKind.Creating or AppStatusKind.Reading or AppStatusKind.Validating => Color.FromRgb(0x2F, 0x7A, 0x8A),
            AppStatusKind.AlreadyExists or AppStatusKind.RemoveTag => Color.FromRgb(0xB0, 0x86, 0x2B),
            AppStatusKind.NoReader => Color.FromRgb(0x8A, 0x5A, 0x2F),
            _ => Color.FromRgb(0x3D, 0x6B, 0x8A)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
