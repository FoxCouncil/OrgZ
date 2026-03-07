// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Data.Converters;
using System.Globalization;

namespace OrgZ.Converters;

public class MediaKindMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaKind kind && parameter is string name)
        {
            return Enum.TryParse<MediaKind>(name, out var target) && kind == target;
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
