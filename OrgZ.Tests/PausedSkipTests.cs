// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using LibVLCSharp.Shared;
using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Skipping tracks while paused has to LAND paused. Stepping through a queue looking for something
/// shouldn't start blasting audio at every stop.
///
/// The decision rests on "is playback paused right now", across two engines that can each be the
/// one in charge, so that's the part pinned here.
/// </summary>
public sealed class PausedSkipTests
{
    // ── The FLAC engine owns playback ────────────────────────────────────

    [Fact]
    public void Flac_engine_paused_counts_as_paused()
    {
        Assert.True(MainWindowViewModel.IsPausedState(flacPaused: true, flacEngineActive: true, vlcState: null));
    }

    [Fact]
    public void Flac_engine_playing_counts_as_playing()
    {
        Assert.False(MainWindowViewModel.IsPausedState(flacPaused: false, flacEngineActive: true, vlcState: null));
    }

    [Fact]
    public void A_stale_libvlc_paused_state_is_ignored_while_the_flac_engine_owns_playback()
    {
        // The adversarial case: libvlc can be sitting in Paused from a previous track while the
        // FLAC engine is the one actually playing. Believing libvlc there would pause a track the
        // user never paused.
        Assert.False(MainWindowViewModel.IsPausedState(flacPaused: false, flacEngineActive: true, vlcState: VLCState.Paused));
    }

    // ── libvlc owns playback ─────────────────────────────────────────────

    [Fact]
    public void Libvlc_paused_counts_as_paused()
    {
        Assert.True(MainWindowViewModel.IsPausedState(flacPaused: false, flacEngineActive: false, vlcState: VLCState.Paused));
    }

    [Theory]
    [InlineData(VLCState.Playing)]
    [InlineData(VLCState.Stopped)]
    [InlineData(VLCState.Ended)]
    [InlineData(VLCState.Error)]
    [InlineData(VLCState.Buffering)]
    [InlineData(VLCState.Opening)]
    [InlineData(VLCState.NothingSpecial)]
    public void No_other_libvlc_state_counts_as_paused(VLCState state)
    {
        // Notably Ended and Stopped: auto-advance at the end of a track runs through the same
        // skip path, and must NOT arm the stay-paused behaviour or the queue would stall.
        Assert.False(MainWindowViewModel.IsPausedState(flacPaused: false, flacEngineActive: false, vlcState: state));
    }

    [Fact]
    public void No_engine_and_no_state_is_not_paused()
    {
        Assert.False(MainWindowViewModel.IsPausedState(flacPaused: false, flacEngineActive: false, vlcState: null));
    }

    [Fact]
    public void A_paused_flac_engine_still_counts_when_it_is_between_tracks()
    {
        // EngineActive goes false the moment the FLAC engine tears down its sink, but its paused
        // flag is what the user's last press actually set - so it still decides.
        Assert.True(MainWindowViewModel.IsPausedState(flacPaused: true, flacEngineActive: false, vlcState: VLCState.Stopped));
    }
}
