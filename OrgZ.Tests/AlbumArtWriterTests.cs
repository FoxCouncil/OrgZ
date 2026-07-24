// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Embedded cover art: writing, replacing, and removing it on real files through
/// TagLib. Verification proves the round-trip; adversarial cases attack files that
/// aren't images, images that lie about their extension, missing paths, absurd sizes,
/// and formats that can't carry art - the writer must refuse cleanly and, critically,
/// never leave the audio file damaged.
/// </summary>
public sealed class AlbumArtWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orgz-art-{Guid.NewGuid():N}");

    public AlbumArtWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // A minimal but genuinely valid 1x1 PNG and JPEG, so TagLib and the sniffer both accept them.
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static byte[] TinyJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==");

    /// <summary>A real WAV TagLib can open and tag.</summary>
    private string MakeAudioFile(string name = "track.wav")
    {
        var path = Path.Combine(_dir, name);

        // 44-byte canonical WAV header + a few samples of silence.
        const int dataBytes = 64;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(44100);
        w.Write(44100 * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        return path;
    }

    private string MakeImage(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static int PictureCount(string audioPath)
    {
        using var f = TagLib.File.Create(audioPath);
        return f.Tag.Pictures?.Length ?? 0;
    }

    // ── Verification ──────────────────────────────────────────

    [Fact]
    public void Adding_art_embeds_a_front_cover_readable_back_from_the_file()
    {
        var audio = MakeAudioFile();
        var image = MakeImage("cover.png", TinyPng());

        var result = AlbumArtWriter.SetArtwork(audio, image);

        Assert.True(result.Ok, result.Error);
        using var f = TagLib.File.Create(audio);
        var picture = Assert.Single(f.Tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal(TinyPng(), picture.Data.Data);
    }

    [Fact]
    public void Jpeg_art_is_stored_with_the_right_mime_type()
    {
        var audio = MakeAudioFile();

        Assert.True(AlbumArtWriter.SetArtwork(audio, MakeImage("cover.jpg", TinyJpeg())).Ok);

        using var f = TagLib.File.Create(audio);
        Assert.Equal("image/jpeg", f.Tag.Pictures[0].MimeType);
    }

    [Fact]
    public void Adding_art_twice_replaces_rather_than_accumulates()
    {
        var audio = MakeAudioFile();

        Assert.True(AlbumArtWriter.SetArtwork(audio, MakeImage("a.png", TinyPng())).Ok);
        Assert.True(AlbumArtWriter.SetArtwork(audio, MakeImage("b.jpg", TinyJpeg())).Ok);

        Assert.Equal(1, PictureCount(audio));
        using var f = TagLib.File.Create(audio);
        Assert.Equal("image/jpeg", f.Tag.Pictures[0].MimeType);   // the newer one won
    }

    [Fact]
    public void Removing_art_strips_it_and_is_idempotent()
    {
        var audio = MakeAudioFile();
        AlbumArtWriter.SetArtwork(audio, MakeImage("cover.png", TinyPng()));
        Assert.Equal(1, PictureCount(audio));

        Assert.True(AlbumArtWriter.RemoveArtwork(audio).Ok);
        Assert.Equal(0, PictureCount(audio));

        // Removing again is a no-op success: the caller asked for "no artwork".
        Assert.True(AlbumArtWriter.RemoveArtwork(audio).Ok);
        Assert.Equal(0, PictureCount(audio));
    }

    [Fact]
    public void Removing_art_from_a_file_that_never_had_any_succeeds()
    {
        Assert.True(AlbumArtWriter.RemoveArtwork(MakeAudioFile()).Ok);
    }

    // ── Adversarial ───────────────────────────────────────────

    [Fact]
    public void A_text_file_renamed_to_png_is_refused_and_the_track_is_untouched()
    {
        var audio = MakeAudioFile();
        var before = File.ReadAllBytes(audio);
        var fake = MakeImage("evil.png", "this is not an image, it is prose"u8.ToArray());

        var result = AlbumArtWriter.SetArtwork(audio, fake);

        Assert.False(result.Ok);
        Assert.Contains("isn't a JPEG or PNG", result.Error);
        Assert.Equal(before, File.ReadAllBytes(audio));   // no half-written tag
        Assert.Equal(0, PictureCount(audio));
    }

    [Fact]
    public void Unsupported_image_extensions_are_refused_before_anything_is_read()
    {
        var audio = MakeAudioFile();

        var result = AlbumArtWriter.SetArtwork(audio, MakeImage("cover.bmp", TinyPng()));

        Assert.False(result.Ok);
        Assert.Contains("JPEG or PNG", result.Error);
        Assert.Equal(0, PictureCount(audio));
    }

    [Fact]
    public void An_empty_image_file_is_refused()
    {
        var audio = MakeAudioFile();

        var result = AlbumArtWriter.SetArtwork(audio, MakeImage("empty.png", []));

        Assert.False(result.Ok);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void An_absurdly_large_image_is_refused_by_size_before_embedding()
    {
        var oversize = new byte[AlbumArtWriter.MaxImageBytes + 1];
        oversize[0] = 0x89; oversize[1] = 0x50; oversize[2] = 0x4E; oversize[3] = 0x47;   // valid PNG magic
        oversize[4] = 0x0D; oversize[5] = 0x0A; oversize[6] = 0x1A; oversize[7] = 0x0A;

        var result = AlbumArtWriter.SetArtwork(MakeAudioFile(), oversize, "image/png");

        Assert.False(result.Ok);
        Assert.Contains("too large", result.Error);
    }

    [Fact]
    public void A_missing_track_file_is_refused_for_both_operations()
    {
        var missing = Path.Combine(_dir, "gone.wav");
        var image = MakeImage("cover.png", TinyPng());

        Assert.False(AlbumArtWriter.SetArtwork(missing, image).Ok);
        Assert.False(AlbumArtWriter.RemoveArtwork(missing).Ok);
    }

    [Fact]
    public void A_missing_image_file_is_refused_and_reported()
    {
        var result = AlbumArtWriter.SetArtwork(MakeAudioFile(), Path.Combine(_dir, "nope.png"));

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void An_audio_file_that_is_not_audio_fails_without_throwing()
    {
        var notAudio = Path.Combine(_dir, "notaudio.wav");
        File.WriteAllText(notAudio, "definitely not a wave file");

        var result = AlbumArtWriter.SetArtwork(notAudio, MakeImage("cover.png", TinyPng()));

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_paths_are_refused_rather_than_throwing(string? path)
    {
        Assert.False(AlbumArtWriter.RemoveArtwork(path!).Ok);
        Assert.False(AlbumArtWriter.SetArtwork(path!, "x.png").Ok);
    }

    // ── Format sniffing ───────────────────────────────────────

    [Fact]
    public void Mime_sniffing_reads_magic_bytes_not_extensions()
    {
        Assert.Equal("image/png", AlbumArtWriter.MimeTypeFor(TinyPng()));
        Assert.Equal("image/jpeg", AlbumArtWriter.MimeTypeFor(TinyJpeg()));
        Assert.Null(AlbumArtWriter.MimeTypeFor("GIF89a"u8.ToArray()));
        Assert.Null(AlbumArtWriter.MimeTypeFor([]));
        Assert.Null(AlbumArtWriter.MimeTypeFor([0x89, 0x50]));            // truncated PNG magic
        Assert.Null(AlbumArtWriter.MimeTypeFor([0xFF, 0xD8]));            // truncated JPEG magic
    }

    [Fact]
    public void Extension_check_accepts_the_three_real_formats_case_insensitively()
    {
        Assert.True(AlbumArtWriter.IsSupportedImage("a.JPG"));
        Assert.True(AlbumArtWriter.IsSupportedImage("a.jpeg"));
        Assert.True(AlbumArtWriter.IsSupportedImage("a.PnG"));
        Assert.False(AlbumArtWriter.IsSupportedImage("a.gif"));
        Assert.False(AlbumArtWriter.IsSupportedImage("a"));
        Assert.False(AlbumArtWriter.IsSupportedImage(""));
    }
}
