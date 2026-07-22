// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput;

namespace OrgZ.Tests;

public class AudioSinkBusTests
{
    private sealed class FakeSink : IAudioSink
    {
        public List<byte[]> Writes { get; } = [];
        public bool IsDisposed { get; private set; }

        public FakeSink(string id, string name = "Fake")
        {
            Id = id;
            DisplayName = name;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public AudioFormat? CurrentFormat { get; private set; }
        public float Volume { get; set; } = 1f;
        public bool IsMuted { get; set; }
        public bool IsOpen => CurrentFormat.HasValue;

        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int FlushCount { get; private set; }
        public int DrainCount { get; private set; }

        public void Open(AudioFormat format) => CurrentFormat = format;
        public void Close() => CurrentFormat = null;
        public void Dispose() { IsDisposed = true; Close(); }

        public void Pause() => PauseCount++;
        public void Resume() => ResumeCount++;
        public void Flush() => FlushCount++;
        public void Drain() => DrainCount++;

        public void Write(ReadOnlySpan<byte> pcm)
        {
            Writes.Add(pcm.ToArray());
        }
    }

    /// <summary>Bytes needed to hit the bus aggregation target for CD-DA (44.1 kHz s16 stereo).</summary>
    private static int TargetBytes => AudioFormat.CdDaStereo16.BytesPerSecond * AudioSinkBus.AggregateTargetMs / 1000;

    private static byte[] PatternedPcm(int length)
    {
        var pcm = new byte[length];
        for (int i = 0; i < length; i++)
        {
            pcm[i] = (byte)(i & 0x7F);
        }
        return pcm;
    }

    [Fact]
    public void Write_Fans_Out_To_Every_Attached_Sink()
    {
        using var bus = new AudioSinkBus();
        var a = new FakeSink("a");
        var b = new FakeSink("b");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(a);
        bus.Add(b);

        var pcm = PatternedPcm(TargetBytes);
        bus.Write(pcm);

        Assert.Single(a.Writes);
        Assert.Single(b.Writes);
        Assert.Equal(pcm, a.Writes[0]);
        Assert.Equal(pcm, b.Writes[0]);
    }

    [Fact]
    public void Small_Writes_Aggregate_Until_Target_Then_Fan_Out_Once()
    {
        using var bus = new AudioSinkBus();
        var sink = new FakeSink("agg");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        // Hi-res sources come out of VLC's resampler in ~6ms slivers; the bus
        // must coalesce them so sinks never see hardware-starving buffer sizes.
        var sliver = PatternedPcm((TargetBytes + 7) / 8);
        for (int i = 0; i < 7; i++)
        {
            bus.Write(sliver);
            Assert.Empty(sink.Writes);
        }
        bus.Write(sliver);

        var block = Assert.Single(sink.Writes);
        Assert.Equal(sliver.Length * 8, block.Length);
        Assert.Equal(sliver, block.Take(sliver.Length));
    }

    [Fact]
    public void Blocks_At_Or_Above_Target_Pass_Through_Immediately()
    {
        using var bus = new AudioSinkBus();
        var sink = new FakeSink("big");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        // 44.1 kHz FLAC delivers ~93ms blocks - those must not pick up extra latency.
        var block = PatternedPcm(TargetBytes * 2);
        bus.Write(block);

        Assert.Single(sink.Writes);
        Assert.Equal(block, sink.Writes[0]);
    }

    [Fact]
    public void DrainAll_Writes_Pending_Tail_Then_Drains_Sinks()
    {
        using var bus = new AudioSinkBus();
        var sink = new FakeSink("drain");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        // End of track: a sub-target sliver is still pending - it's the last
        // audio of the song and must reach the sink, not get dropped.
        var tail = PatternedPcm(TargetBytes / 4);
        bus.Write(tail);
        Assert.Empty(sink.Writes);

        bus.DrainAll();

        var block = Assert.Single(sink.Writes);
        Assert.Equal(tail, block);
        Assert.Equal(1, sink.DrainCount);
        Assert.Equal(0, sink.FlushCount);
    }

    [Fact]
    public void DrainAll_With_Nothing_Pending_Still_Drains_Sinks()
    {
        using var bus = new AudioSinkBus();
        var sink = new FakeSink("drain-empty");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        bus.DrainAll();

        Assert.Empty(sink.Writes);
        Assert.Equal(1, sink.DrainCount);
    }

    [Fact]
    public void FlushAll_Drops_Pending_Aggregated_Audio()
    {
        using var bus = new AudioSinkBus();
        var sink = new FakeSink("flush");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        // Seek: a sliver of the old position is pending - it must not leak
        // into the audio that fans out after the flush.
        var stale = new byte[TargetBytes / 2];
        Array.Fill(stale, (byte)0xEE);
        bus.Write(stale);
        bus.FlushAll();

        var fresh = PatternedPcm(TargetBytes);
        bus.Write(fresh);

        var block = Assert.Single(sink.Writes);
        Assert.Equal(fresh, block);
    }

    [Fact]
    public void Add_Deduplicates_By_Sink_Id()
    {
        using var bus = new AudioSinkBus();
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(new FakeSink("same"));
        bus.Add(new FakeSink("same"));
        Assert.Single(bus.Sinks);
    }

    [Fact]
    public void Remove_Disposes_Sink()
    {
        using var bus = new AudioSinkBus();
        var sink = new FakeSink("x");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        bus.Remove("x");

        Assert.Empty(bus.Sinks);
        Assert.True(sink.IsDisposed);
    }

    [Fact]
    public void SetFormat_Opens_All_Sinks_To_New_Format()
    {
        using var bus = new AudioSinkBus();
        var a = new FakeSink("a");
        bus.Add(a);
        bus.SetFormat(AudioFormat.CdDaStereo16);

        Assert.True(a.IsOpen);
        Assert.Equal(44100, a.CurrentFormat!.Value.SampleRate);
    }

    [Fact]
    public void MasterVolume_Scales_Samples_Before_Fanout()
    {
        using var bus = new AudioSinkBus
        {
            MasterVolume = 0.5f,
        };
        var sink = new FakeSink("scaled");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        // First two S16 samples: [1000, 2000] → at 50% → [500, 1000]
        var source = new byte[TargetBytes];
        BitConverter.GetBytes((short)1000).CopyTo(source, 0);
        BitConverter.GetBytes((short)2000).CopyTo(source, 2);

        bus.Write(source);

        Assert.Single(sink.Writes);
        var seen = sink.Writes[0];
        var s1 = BitConverter.ToInt16(seen, 0);
        var s2 = BitConverter.ToInt16(seen, 2);
        Assert.InRange(s1, 499, 501);
        Assert.InRange(s2, 999, 1001);
    }

    [Fact]
    public void MasterVolume_At_Unity_Passes_Samples_Unchanged()
    {
        using var bus = new AudioSinkBus { MasterVolume = 1f };
        var sink = new FakeSink("passthrough");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(sink);

        var source = PatternedPcm(TargetBytes);
        bus.Write(source);

        Assert.Equal(source, sink.Writes[0]);
    }

    [Fact]
    public void Clear_Disposes_All_Sinks_And_Empties_List()
    {
        using var bus = new AudioSinkBus();
        var a = new FakeSink("a");
        var b = new FakeSink("b");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(a);
        bus.Add(b);

        bus.Clear();

        Assert.Empty(bus.Sinks);
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
    }

    [Fact]
    public void PauseAll_ResumeAll_FlushAll_Fan_Out_To_Every_Sink()
    {
        using var bus = new AudioSinkBus();
        var a = new FakeSink("a");
        var b = new FakeSink("b");
        bus.SetFormat(AudioFormat.CdDaStereo16);
        bus.Add(a);
        bus.Add(b);

        bus.PauseAll();
        bus.PauseAll();
        bus.ResumeAll();
        bus.FlushAll();

        Assert.Equal(2, a.PauseCount);
        Assert.Equal(2, b.PauseCount);
        Assert.Equal(1, a.ResumeCount);
        Assert.Equal(1, b.ResumeCount);
        Assert.Equal(1, a.FlushCount);
        Assert.Equal(1, b.FlushCount);
    }

    [Fact]
    public void AudioDeviceInfo_QualifiedId_RoundTrips()
    {
        var info = new AudioDeviceInfo
        {
            DeviceId = "0",
            DisplayName = "Test",
            ProviderId = "waveout",
            ProviderName = "Wave Out",
        };
        Assert.Equal("waveout:0", info.QualifiedId);

        var (providerId, deviceId) = AudioDeviceInfo.SplitQualified(info.QualifiedId);
        Assert.Equal("waveout", providerId);
        Assert.Equal("0", deviceId);
    }

    [Fact]
    public void AudioDeviceInfo_SplitQualified_Handles_Missing_Separator()
    {
        var (providerId, deviceId) = AudioDeviceInfo.SplitQualified("nothing");
        Assert.Equal("", providerId);
        Assert.Equal("nothing", deviceId);
    }
}
