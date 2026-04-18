// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Globalization;
using Avalonia.Controls;
using OrgZ.Converters;

namespace OrgZ.Tests;

public class DoubleToStarGridLengthConverterTests
{
    private readonly DoubleToStarGridLengthConverter _conv = new();

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(2.5)]
    [InlineData(100.0)]
    public void Convert_passes_double_through_as_star_grid_length(double input)
    {
        var result = _conv.Convert(input, typeof(GridLength), null, CultureInfo.InvariantCulture);
        var gl = Assert.IsType<GridLength>(result);
        Assert.True(gl.IsStar);
        Assert.Equal(input, gl.Value, precision: 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.0001)]
    public void Convert_clamps_below_threshold_to_minimum(double input)
    {
        // The 0.001 floor exists so a 0% bar segment doesn't collapse to nothing visually
        var result = _conv.Convert(input, typeof(GridLength), null, CultureInfo.InvariantCulture);
        var gl = Assert.IsType<GridLength>(result);
        Assert.Equal(0.001, gl.Value, precision: 6);
    }

    [Fact]
    public void Convert_non_double_input_falls_back_to_zero_then_clamps()
    {
        var result = _conv.Convert("garbage", typeof(GridLength), null, CultureInfo.InvariantCulture);
        var gl = Assert.IsType<GridLength>(result);
        Assert.Equal(0.001, gl.Value, precision: 6);
    }

    [Fact]
    public void Convert_null_input_clamps_to_minimum()
    {
        var result = _conv.Convert(null, typeof(GridLength), null, CultureInfo.InvariantCulture);
        var gl = Assert.IsType<GridLength>(result);
        Assert.Equal(0.001, gl.Value, precision: 6);
    }

    [Fact]
    public void ConvertBack_throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            _conv.ConvertBack(new GridLength(1.0, GridUnitType.Star), typeof(double), null, CultureInfo.InvariantCulture));
    }
}

public class MediaKindMatchConverterTests
{
    private readonly MediaKindMatchConverter _conv = new();

    [Theory]
    [InlineData(MediaKind.Music, "Music", true)]
    [InlineData(MediaKind.Music, "Radio", false)]
    [InlineData(MediaKind.Radio, "Radio", true)]
    [InlineData(MediaKind.Radio, "Music", false)]
    public void Convert_matches_kind_against_parameter_name(MediaKind value, string param, bool expected)
    {
        var result = _conv.Convert(value, typeof(bool), param, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_returns_false_when_parameter_not_a_kind_name()
    {
        var result = _conv.Convert(MediaKind.Music, typeof(bool), "NotAValidKind", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_returns_false_when_value_not_a_MediaKind()
    {
        var result = _conv.Convert("not-a-kind", typeof(bool), "Music", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_returns_false_when_parameter_null()
    {
        var result = _conv.Convert(MediaKind.Music, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void ConvertBack_throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            _conv.ConvertBack(true, typeof(MediaKind), "Music", CultureInfo.InvariantCulture));
    }
}
