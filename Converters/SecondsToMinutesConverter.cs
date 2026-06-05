// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Globalization;
using Avalonia.Data.Converters;

namespace OrgZ.Converters;

/// <summary>
/// Formats an integer seconds value as a human-friendly duration: "32 min",
/// "1 h 5 min". Used on podcast episode rows where the API returns seconds.
/// </summary>
public sealed class SecondsToMinutesConverter : IValueConverter
{
    public static readonly SecondsToMinutesConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int seconds = value switch
        {
            int i  => i,
            long l => (int)l,
            double d => (int)d,
            _ => 0,
        };
        if (seconds <= 0) return "";
        var min = seconds / 60;
        if (min < 60) return $"{min} min";
        var h = min / 60;
        var m = min % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
