// Copyright (c) 2025 Fox Diller

using Avalonia.Data.Converters;
using System.Globalization;

namespace OrgZ.Converters;

/// <summary>
/// Converts file size in bytes to a human-readable format
/// </summary>
public class FileSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            return "0 B";
        }

        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
        {
            return 0L;
        }

        string[] parts = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            return 0L;
        }

        if (!double.TryParse(parts[0], out double num))
        {
            return 0L;
        }

        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = Array.IndexOf(sizes, parts[1].ToUpperInvariant());

        if (order < 0)
        {
            return 0L;
        }

        for (int i = 0; i < order; i++)
        {
            num *= 1024;
        }

        return (long)num;
    }
}
