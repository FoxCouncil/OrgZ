// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OrgZ.Converters;

/// <summary>
/// Converts an ISO 3166-1 alpha-2 country code (e.g. "US", "GB", "DE") into a
/// cached <see cref="Bitmap"/> for the matching flag PNG bundled under
/// <c>Assets/Flags/</c>. Empty / unknown codes return null so the Image cell
/// stays empty. Bitmaps are cached so the DataGrid's row recycling doesn't
/// re-decode the same PNG every scroll.
/// </summary>
public sealed class CountryCodeToFlagConverter : IValueConverter
{
    public static readonly CountryCodeToFlagConverter Instance = new();

    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var key = code.Trim().ToLowerInvariant();
        return _cache.GetOrAdd(key, static k =>
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Orgz/Assets/Flags/{k}.png"));
                return new Bitmap(stream);
            }
            catch
            {
                // No PNG bundled for this code -- the cell renders empty,
                // matching how unknown codes used to fall back to plain text.
                return null;
            }
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
