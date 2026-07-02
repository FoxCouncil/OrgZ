// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// MACHINE-GATED smoke tests that actually EXECUTE the external encoders (ffmpeg / flac / lame)
/// the rest of the suite only argues with - argument-string tests can't tell you the tool rejects
/// the arguments. Each test no-ops when its tool isn't on PATH (CI runners may not carry them; a
/// dev box with the bundled toolchain runs everything). This is the documented exception to the
/// no-silent-gating rule: the gate is an external binary, not product state, and the honest
/// coverage lives here rather than not existing at all.
/// </summary>
public class LocalToolSmokeTests
{
    // ── ffmpeg: the FLAC→ALAC transcode path every non-MP3 library import takes ──

    [Fact]
    public async Task Ffmpeg_transcodes_a_flac_library_track_to_alac_on_a_binary_ipod()
    {
        var ffmpeg = ToolOnPath("ffmpeg");
        if (ffmpeg is null) { return; } // tool absent on this machine

        var mount = Path.Combine(Path.GetTempPath(), "orgz-smoke-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-smokesrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            var iTunesDir = Path.Combine(mount, "iPod_Control", "iTunes");
            Directory.CreateDirectory(iTunesDir);
            Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "Music"));
            var doc = ITunesDbWriter.CreateEmpty();
            ITunesDbChunkTree.Normalize(doc.Root);
            File.WriteAllBytes(Path.Combine(iTunesDir, "iTunesDB"), ITunesDbChunkTree.Serialize(doc));

            // FLAC source (WAV is natively iPod-playable and would passthrough) - the test is
            // ffmpeg-gated anyway, so ffmpeg fabricates the source too.
            var wav = Path.Combine(srcDir, "Space Boy.wav");
            WritePcmWav(wav, seconds: 0.25);
            var flac = Path.Combine(srcDir, "Space Boy.flac");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg, $"-y -i \"{wav}\" \"{flac}\"") { UseShellExecute = false, RedirectStandardError = true })!)
            {
                await p.WaitForExitAsync();
                Assert.Equal(0, p.ExitCode);
            }

            // The real import path: FLAC isn't natively iPod-compatible, so this transcodes to ALAC
            // via ffmpeg, copies the .m4a onto the device, and commits the DB.
            var result = await IPodTrackImporter.ImportAsync(mount, flac, ffmpeg, "Video 5.5G", fireWireGuid: null);

            Assert.EndsWith(".m4a", result.DestFile, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(result.DestFile).Length > 0);

            ITunesDbReader.ReadAll(Path.Combine(iTunesDir, "iTunesDB"), mount, out var tracks, out _);
            var t = Assert.Single(tracks);
            Assert.Equal("Space Boy", t.Title);
        }
        finally
        {
            TryDelete(mount);
            TryDelete(srcDir);
        }
    }

    // ── flac / lame: the CD rip encoders, fed real PCM through the product pipeline ──

    [Fact]
    public async Task Flac_encoder_produces_a_real_flac_stream()
    {
        if (ToolOnPath("flac") is null) { return; } // tool absent on this machine
        var outPath = await EncodePcm(RipFormat.Flac, ".flac");
        try
        {
            var head = ReadHead(outPath, 4);
            Assert.Equal("fLaC"u8.ToArray(), head);   // FLAC stream marker
        }
        finally { TryDeleteFile(outPath); }
    }

    [Fact]
    public async Task Lame_encoder_produces_a_real_mp3_stream()
    {
        if (ToolOnPath("lame") is null) { return; } // tool absent on this machine
        var outPath = await EncodePcm(RipFormat.Mp3, ".mp3");
        try
        {
            var head = ReadHead(outPath, 3);
            // Either an ID3 tag (lame writes one for tagged tracks) or a bare MPEG frame sync.
            bool id3 = head[0] == 'I' && head[1] == 'D' && head[2] == '3';
            bool sync = head[0] == 0xFF && (head[1] & 0xE0) == 0xE0;
            Assert.True(id3 || sync, $"not an MP3 stream: {head[0]:X2} {head[1]:X2} {head[2]:X2}");
        }
        finally { TryDeleteFile(outPath); }
    }

    private static async Task<string> EncodePcm(RipFormat format, string ext)
    {
        var pcm = MakePcm(seconds: 0.25);
        var outPath = Path.Combine(Path.GetTempPath(), "orgz-enc-" + Guid.NewGuid().ToString("N") + ext);
        var metadata = new RipTrackMetadata { Title = "Smoke", Artist = "OrgZ", Album = "Smoke Tests", TrackNumber = 1 };
        var options = new CdRipOptions { Format = format };

        await using (var enc = RipEncoder.Open(outPath, pcm.Length, metadata, options))
        {
            await enc.WriteAsync(pcm, CancellationToken.None);
            await enc.CompleteAsync(CancellationToken.None);
        }

        Assert.True(File.Exists(outPath) && new FileInfo(outPath).Length > 0, "encoder produced no output");
        return outPath;
    }

    // ── plumbing ──

    private static string? ToolOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) { continue; }
            foreach (var candidate in new[] { Path.Combine(dir.Trim(), name + ".exe"), Path.Combine(dir.Trim(), name) })
            {
                try { if (File.Exists(candidate)) { return candidate; } } catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    /// <summary>0.25s of 440 Hz sine, 44.1 kHz 16-bit stereo - real audio every encoder accepts.</summary>
    private static byte[] MakePcm(double seconds)
    {
        int frames = (int)(44100 * seconds);
        var pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 12000);
            pcm[i * 4] = (byte)(s & 0xFF);
            pcm[i * 4 + 1] = (byte)((s >> 8) & 0xFF);
            pcm[i * 4 + 2] = pcm[i * 4];
            pcm[i * 4 + 3] = pcm[i * 4 + 1];
        }
        return pcm;
    }

    private static void WritePcmWav(string path, double seconds)
    {
        var pcm = MakePcm(seconds);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
    }

    private static byte[] ReadHead(string path, int n)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[n];
        fs.ReadExactly(b);
        return b;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { /* temp cleanup */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* temp cleanup */ }
    }
}
