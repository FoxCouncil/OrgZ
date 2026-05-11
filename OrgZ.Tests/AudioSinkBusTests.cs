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

        public void Open(AudioFormat format) => CurrentFormat = format;
        public void Close() => CurrentFormat = null;
        public void Dispose() { IsDisposed = true; Close(); }

        public void Pause() => PauseCount++;
        public void Resume() => ResumeCount++;
        public void Flush() => FlushCount++;

        public void Write(ReadOnlySpan<byte> pcm)
        {
            Writes.Add(pcm.ToArray());
        }
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

        var pcm = new byte[] { 1, 2, 3, 4 };
        bus.Write(pcm);

        Assert.Single(a.Writes);
        Assert.Single(b.Writes);
        Assert.Equal(pcm, a.Writes[0]);
        Assert.Equal(pcm, b.Writes[0]);
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

        // Two S16 samples = 4 bytes.  Source: [1000, 2000] → at 50% → [500, 1000]
        var source = new byte[4];
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

        var source = new byte[] { 10, 20, 30, 40 };
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
