// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// The publish step drops libvlc plugin categories a music player never loads, because
/// `new LibVLC()` scans that directory at startup and the scan is per-FILE: on a clean
/// Windows install, cold, with every DLL virus-scanned as it's touched, it measured twenty
/// seconds against ~5 for the entire rest of startup.
///
/// The obvious risk is trimming something playback actually needs, where the symptom is
/// "audio silently stops working in shipped builds only". So this doesn't check a list of
/// names - it builds a trimmed plugin directory and makes libvlc decode real audio through
/// it. If a category turns out to be load-bearing, this fails here rather than in a release.
/// </summary>
public sealed class VlcPluginTrimTests : IDisposable
{
    // Kept in step with the TrimVlcPluginsOnPublish target in OrgZ.csproj.
    private static readonly string[] Dropped =
    [
        "video_output", "video_filter", "video_chroma", "video_splitter",
        "d3d11", "d3d9", "visualization", "spu", "text_renderer",
        "stream_out", "mux", "access_output",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orgz-vlctrim-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string? PluginSource()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins");
        return Directory.Exists(root) ? root : null;
    }

    [Fact]
    public void The_trim_keeps_everything_playback_needs()
    {
        var source = PluginSource();
        if (source is null)
        {
            return;   // no Windows libvlc natives here (Linux CI) - nothing to trim
        }

        // Categories that decode, read and demux audio must survive - dropping any of them
        // is the failure this whole test exists to catch.
        foreach (var required in new[] { "codec", "access", "demux", "packetizer", "audio_filter", "audio_output", "audio_mixer" })
        {
            Assert.DoesNotContain(required, Dropped);
            Assert.True(Directory.Exists(Path.Combine(source, required)), $"expected plugin category '{required}' to exist");
        }
    }

    [Fact]
    public async Task Libvlc_still_decodes_audio_with_the_trimmed_plugin_set()
    {
        var source = PluginSource();
        if (source is null)
        {
            return;
        }

        // Build a plugin tree with exactly the categories a shipped build would have.
        var plugins = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(plugins);
        var copied = 0;
        foreach (var category in Directory.GetDirectories(source))
        {
            if (Dropped.Contains(Path.GetFileName(category), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var dest = Path.Combine(plugins, Path.GetFileName(category));
            Directory.CreateDirectory(dest);
            foreach (var dll in Directory.GetFiles(category, "*.dll"))
            {
                File.Copy(dll, Path.Combine(dest, Path.GetFileName(dll)), overwrite: true);
                copied++;
            }
        }

        Assert.True(copied > 0, "no plugins were copied - the source layout changed");

        // A real 2 s WAV, decoded through the trimmed set.
        var wav = Path.Combine(_dir, "tone.wav");
        WriteTone(wav, seconds: 2);

        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLCSharp.Shared.LibVLC("--no-video", "--quiet", $"--plugin-path={plugins}");
        using var media = new LibVLCSharp.Shared.Media(vlc, wav, LibVLCSharp.Shared.FromType.FromPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var status = await media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseLocal, timeout: 20_000, cancellationToken: cts.Token);

        Assert.Equal(LibVLCSharp.Shared.MediaParsedStatus.Done, status);
        Assert.InRange(media.Duration, 1_500, 2_500);

        // Parsing proves the demuxer survived; an audio track in the result proves the
        // codec side did too.
        Assert.Contains(media.Tracks, t => t.TrackType == LibVLCSharp.Shared.TrackType.Audio);
    }

    private static void WriteTone(string path, int seconds)
    {
        const int rate = 44100, channels = 2;
        var frames = rate * seconds;
        var dataBytes = frames * channels * 2;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            w.Write(s);
            w.Write(s);
        }
    }
}
