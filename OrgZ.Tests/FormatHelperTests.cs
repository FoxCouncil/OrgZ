// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

public class FormatHelperTests
{
    // ===== FormatFileSize — binary KB units, "F0" for bytes, "F2" for everything else =====

    [Theory]
    [InlineData(0L,             "0 B")]
    [InlineData(512L,           "512 B")]
    [InlineData(1023L,          "1023 B")]
    [InlineData(1024L,          "1.00 KB")]
    [InlineData(2048L,          "2.00 KB")]
    [InlineData(1_048_576L,     "1.00 MB")]      // 1 MiB exactly
    [InlineData(1_500_000L,     "1.43 MB")]
    [InlineData(80_000_000_000L,"74.51 GB")]     // Apple decimal 80GB → 74.5 GiB
    [InlineData(1_099_511_627_776L, "1.00 TB")]  // 1 TiB exactly
    public void FormatFileSize_uses_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatFileSize(bytes));
    }

    [Fact]
    public void FormatFileSize_caps_at_TB()
    {
        // Above the unit table, formatting still returns TB
        Assert.EndsWith("TB", FormatHelper.FormatFileSize(2_000_000_000_000_000L));
    }

    // ===== FormatTimeSpan — "m:ss.fff" under an hour, "h:mm:ss.fff" otherwise =====

    [Theory]
    [InlineData(0,    0,    0,    0,   "0:00.000")]
    [InlineData(0,    0,    3,    250, "0:03.250")]
    [InlineData(0,    5,   30,    0,   "5:30.000")]
    [InlineData(0,   59,   59,  999,  "59:59.999")]
    [InlineData(1,    0,    0,    0,   "1:00:00.000")]
    [InlineData(2,   30,   45,  100,   "2:30:45.100")]
    public void FormatTimeSpan_picks_format_by_hours(int h, int m, int s, int ms, string expected)
    {
        var ts = new TimeSpan(0, h, m, s, ms);
        Assert.Equal(expected, FormatHelper.FormatTimeSpan(ts));
    }

    // ===== TryParseTimeSpan — accepts seconds-only, m:ss, h:mm:ss with optional fractions =====

    [Theory]
    [InlineData("3:25",       0, 3, 25, 0)]
    [InlineData("3:25.500",   0, 3, 25, 500)]
    [InlineData("1:30:00",    1, 30, 0, 0)]
    [InlineData("0:30",       0, 0, 30, 0)]
    public void TryParseTimeSpan_parses_known_forms(string text, int h, int m, int s, int ms)
    {
        var result = FormatHelper.TryParseTimeSpan(text);
        Assert.NotNull(result);
        var expected = new TimeSpan(0, h, m, s, ms);
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a duration")]
    public void TryParseTimeSpan_returns_null_for_garbage(string? text)
    {
        Assert.Null(FormatHelper.TryParseTimeSpan(text));
    }

    // ===== FormatTimeAgo — bucketed thresholds: just now / m / h / d / mo / y =====

    [Theory]
    [InlineData(0,                  "just now")]   // 0 sec
    [InlineData(30,                 "just now")]   // < 1 min
    [InlineData(60,                 "1m ago")]
    [InlineData(60 * 30,            "30m ago")]
    [InlineData(60 * 60,            "1h ago")]
    [InlineData(60 * 60 * 5,        "5h ago")]
    [InlineData(60 * 60 * 24,       "1d ago")]
    [InlineData(60 * 60 * 24 * 7,   "7d ago")]
    [InlineData(60 * 60 * 24 * 30,  "1mo ago")]
    [InlineData(60 * 60 * 24 * 90,  "3mo ago")]
    [InlineData(60 * 60 * 24 * 365, "1y ago")]
    [InlineData(60 * 60 * 24 * 730, "2y ago")]
    public void FormatTimeAgo_buckets_by_threshold(int seconds, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatTimeAgo(TimeSpan.FromSeconds(seconds)));
    }

    // ===== FormatDateWithRelative — "g" + parenthesized relative =====

    [Fact]
    public void FormatDateWithRelative_returns_dash_for_null()
    {
        Assert.Equal("-", FormatHelper.FormatDateWithRelative(null));
    }

    [Fact]
    public void FormatDateWithRelative_includes_local_time_and_relative()
    {
        var date = DateTime.UtcNow.AddHours(-2);
        var formatted = FormatHelper.FormatDateWithRelative(date);

        Assert.Contains("(", formatted);
        Assert.Contains("h ago", formatted);   // 2h ago bucket
    }
}
