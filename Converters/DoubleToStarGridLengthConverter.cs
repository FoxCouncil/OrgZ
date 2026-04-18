// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Data.Converters;
using System.Globalization;

namespace OrgZ.Converters;

public class DoubleToStarGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var d = value is double dv ? dv : 0.0;
        if (d < 0.001)
        {
            d = 0.001;
        }
        return new GridLength(d, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
