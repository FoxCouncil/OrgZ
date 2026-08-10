// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// Apple binary plists - the body format of AirPlay 2's /info and stream SETUP. Round
/// trips prove the encoding; the header/trailer assertions pin the parts a receiver reads
/// first and would reject outright.
/// </summary>
public class BinaryPlistTests
{
    private static object? RoundTrip(object? value) => BinaryPlist.Read(BinaryPlist.Write(value));

    [Fact]
    public void Output_starts_with_the_bplist_magic_and_carries_a_32_byte_trailer()
    {
        var bytes = BinaryPlist.Write(new Dictionary<string, object?> { ["a"] = 1L });

        Assert.Equal("bplist00"u8.ToArray(), bytes[..8]);
        Assert.True(bytes.Length > 40);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65535L)]
    [InlineData(70000L)]
    [InlineData(7000000000L)]
    [InlineData(-1L)]
    public void Integers_round_trip_across_every_width(long value)
    {
        Assert.Equal(value, RoundTrip(value));
    }

    [Fact]
    public void Booleans_and_strings_round_trip()
    {
        Assert.Equal(true, RoundTrip(true));
        Assert.Equal(false, RoundTrip(false));
        Assert.Equal("timingProtocol", RoundTrip("timingProtocol"));
        Assert.Equal("", RoundTrip(""));
    }

    [Fact]
    public void A_long_string_uses_the_spill_length()
    {
        // Past 14 characters the length moves into a follow-on integer - the branch that
        // silently truncates if the reader and writer disagree.
        var text = new string('x', 300);
        Assert.Equal(text, RoundTrip(text));
    }

    [Fact]
    public void Non_ascii_survives_as_utf16()
    {
        Assert.Equal("Fox’s HomePod", RoundTrip("Fox’s HomePod"));
    }

    [Fact]
    public void Data_round_trips_including_the_32_byte_audio_key_case()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(key, RoundTrip(key));

        var big = Enumerable.Range(0, 500).Select(i => (byte)(i & 0xFF)).ToArray();
        Assert.Equal(big, RoundTrip(big));
    }

    [Fact]
    public void Doubles_round_trip()
    {
        Assert.Equal(44100.0, RoundTrip(44100.0));
    }

    [Fact]
    public void A_setup_shaped_plist_round_trips_whole()
    {
        // The real shape: a stream SETUP body carrying the raw audio key and format ids.
        var shk = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var setup = new Dictionary<string, object?>
        {
            ["timingProtocol"] = "None",
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 96L,             // audio
                    ["audioFormat"] = 262144L,  // 0x40000 = ALAC 44.1/16/2
                    ["ct"] = 2L,                // compression: ALAC
                    ["shk"] = shk,              // the raw 32-byte audio key
                    ["spf"] = 352L,             // samples per frame
                    ["isMedia"] = true,
                },
            },
        };

        var result = Assert.IsType<Dictionary<string, object?>>(RoundTrip(setup));
        Assert.Equal("None", result["timingProtocol"]);

        var streams = Assert.IsType<List<object?>>(result["streams"]);
        var stream = Assert.IsType<Dictionary<string, object?>>(streams[0]);
        Assert.Equal(96L, stream["type"]);
        Assert.Equal(262144L, stream["audioFormat"]);
        Assert.Equal(2L, stream["ct"]);
        Assert.Equal(352L, stream["spf"]);
        Assert.Equal(true, stream["isMedia"]);
        Assert.Equal(shk, stream["shk"]);
    }

    [Fact]
    public void A_reply_shaped_plist_round_trips()
    {
        // What SETUP answers with - the ports we then stream to.
        var reply = new Dictionary<string, object?>
        {
            ["eventPort"] = 7011L,
            ["timingPort"] = 0L,
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?> { ["dataPort"] = 51000L, ["controlPort"] = 51001L, ["type"] = 96L },
            },
        };

        var result = Assert.IsType<Dictionary<string, object?>>(RoundTrip(reply));
        var stream = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<List<object?>>(result["streams"])[0]);

        Assert.Equal(7011L, result["eventPort"]);
        Assert.Equal(51000L, stream["dataPort"]);
        Assert.Equal(51001L, stream["controlPort"]);
    }

    [Fact]
    public void A_dictionary_past_fourteen_entries_uses_the_spill_length()
    {
        var big = new Dictionary<string, object?>();
        for (var i = 0; i < 40; i++)
        {
            big[$"key{i}"] = (long)i;
        }

        var result = Assert.IsType<Dictionary<string, object?>>(RoundTrip(big));

        Assert.Equal(40, result.Count);
        Assert.Equal(39L, result["key39"]);
    }

    [Fact]
    public void Garbage_is_rejected_rather_than_half_parsed()
    {
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(new byte[8]));
        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read("not a plist at all......."u8.ToArray()));
    }

    [Fact]
    public void An_unsupported_type_throws_instead_of_emitting_a_misread_plist()
    {
        Assert.Throws<NotSupportedException>(() => BinaryPlist.Write(new object()));
    }
}
