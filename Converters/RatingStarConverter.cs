// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OrgZ.Converters;

/// <summary>
/// True when a rating reaches the star position passed as the converter parameter.
/// value = the item's rating (nullable int), parameter = the 1-based star slot (1..5).
/// Used by the Rating column to layer a lit star over its dark placeholder.
/// </summary>
public sealed class RatingStarConverter : IValueConverter
{
    public static readonly RatingStarConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rating = value switch
        {
            int i => i,
            _ => 0,
        };
        int position = parameter switch
        {
            int p => p,
            string s when int.TryParse(s, out var ps) => ps,
            _ => 0,
        };
        return position > 0 && rating >= position;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
