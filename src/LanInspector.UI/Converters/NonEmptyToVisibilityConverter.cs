using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LanInspector.UI.Converters;

/// <summary>
/// Collapses an element when its bound string is empty. Used for the optional detail lines on a
/// critical device (Tailscale address, "address changed" note) so that a device with nothing extra
/// to say does not leave gaps and stray separators in the row.
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("NonEmptyToVisibilityConverter is one-way.");
    }
}
