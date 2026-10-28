using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VibeShift;

/// <summary>true -> Collapsed, false -> Visible (for "nothing here yet" placeholders).</summary>
public sealed class InverseVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
