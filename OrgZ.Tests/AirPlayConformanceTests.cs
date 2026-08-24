// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;
using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// Isolates the sender's PROCESS-WIDE state from the machine the suite runs on.
///
/// Every AirPlay session touches <see cref="DacpControlServer"/>, which reads the DACP id out
/// of the real settings file and publishes <c>iTunes_Ctrl_&lt;id&gt;</c> on the LAN. Left
/// alone, a test run announces a second, shorter-lived answer for the name the developer's
/// running app advertises - and when the test host exits without a goodbye the receiver is
/// left resolving the control endpoint to a dead port, which is exactly the "Controls Not
/// Available" this code exists to avoid. So: our own settings directory, no mDNS at all, and
/// the endpoint shut down when the class is done with it.
/// </summary>
public sealed class AirPlaySenderFixture : IDisposable
{
    private readonly string _settingsDirectory;
    private readonly bool _publishMdns;

    public AirPlaySenderFixture()
    {
        _settingsDirectory = Path.Combine(Path.GetTempPath(), "OrgZ-airplay-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_settingsDirectory);

        Settings.OverrideSettingsDirectory(_settingsDirectory);

        // Restored in Dispose: this is a production static, and leaving it false would quietly
        // un-advertise the control endpoint for every test class that runs after this one.
        _publishMdns = DacpControlServer.PublishMdns;
        DacpControlServer.PublishMdns = false;
    }

    public void Dispose()
    {
        DacpControlServer.Shutdown();
        DacpControlServer.PublishMdns = _publishMdns;
        Settings.OverrideSettingsDirectory(null);

        try
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is not a test failure.
        }
    }
}

/// <summary>
/// Drives the real <see cref="AirPlay2Session"/> against <see cref="FakeAirPlayReceiver"/>
/// on loopback and asserts what the receiver would DISPLAY - not what it answered.
///
/// This is where the sender's protocol gets developed. Every assertion here encodes a wire
/// fact that was previously only checkable by streaming at a speaker and looking at a phone:
/// a push whose keys the receiver can't name is 200-accepted and renders NOTHING, and a
/// "replace" push full of such keys erases the tile the DMAP path just painted. The fake
/// receiver models exactly that, so a regression in any of it fails HERE, in seconds,
/// naming the byte - never again as "the display is blank" forty hardware cycles later.
///
/// What the sender actually says is deliberately LEAN, and these tests pin that shape:
/// the tile is painted entirely by the DMAP path (progress, then text, then artwork), the
/// only MediaRemote push on an announce is the iPhone's six transport commands, and the
/// now-playing/client/state pushes are sent ONLY as the pause and resume timeline pin. The
/// receiver 400s the rest, so sending more is how the tile goes dark.
/// </summary>
[Collection(RealSocketCollection.Name)]
public class AirPlayConformanceTests : IClassFixture<AirPlaySenderFixture>
{
    private static readonly byte[] Silence = new byte[AirPlay2Session.FramesPerPacket * 4];

    /// <summary>The MediaRemote push types the sender must NEVER send on an announce.</summary>
    private static readonly string[] RemovedPushes = ["updateMRNowPlayingInfo", "updateMRNowPlayingClient", "updateMRPlaybackState"];

    private const string NowPlayingKey = "kMRMediaRemoteNowPlayingInfo";

    private static async Task<AirPlay2Session> ConnectAndStreamAsync(FakeAirPlayReceiver fake)
    {
        var session = new AirPlay2Session(fake.Host, fake.Port);
        await session.ConnectAsync(CancellationToken.None);

        // Audio before any announcement: metadata is anchored to an RTP timestamp, which
        // means nothing in a stream that has never sent a packet.
        for (var i = 0; i < 20; i++)
        {
            await session.SendPacketAsync(Silence, CancellationToken.None);
        }

        return session;
    }

    [Fact]
    public async Task Now_playing_tile_gets_title_artist_album_and_artwork()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());

        fake.WaitFor(() => fake.Metadata.Count > 0, "the now-playing tile to paint");
        fake.WaitFor(() => fake.Artwork.Count > 0, "the cover to arrive");

        var metadata = fake.Metadata[^1];
        Assert.Equal("Golden Record", metadata.Title);
        Assert.Equal("Voyager", metadata.Artist);
        Assert.Equal("Sounds of Earth", metadata.Album);
        Assert.Equal(180000, metadata.DurationMs);

        var artwork = fake.Artwork[^1];
        Assert.Equal("image/jpeg", artwork.ContentType);
        Assert.Equal(Jpeg(), artwork.Bytes);

        // Text and cover describe the SAME moment. An artwork update anchored anywhere else
        // belongs to an item the receiver has already moved past, and it drops it.
        Assert.NotNull(metadata.RtpTime);
        Assert.Equal(metadata.RtpTime, artwork.RtpTime);

        // The catch-all: the real receiver drops what it can't read WITH a 200, so the only
        // trustworthy assertion is that nothing was dropped.
        Assert.Empty(fake.Rejected);
    }

    /// <summary>
    /// The pushes that were REMOVED, and must stay removed.
    ///
    /// A real receiver answers a now-playing/client/state push on an announce with a 400 and
    /// renders nothing; the tile is the DMAP path's job. This is the test that fails if
    /// someone reads the MediaRemote documentation and helpfully puts them back.
    /// </summary>
    [Fact]
    public async Task Announcing_a_track_sends_no_media_remote_now_playing_pushes()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());
        fake.WaitFor(() => fake.SupportedCommands.Count == 6, "the transport commands to arrive");

        Assert.All(fake.Commands, command => Assert.Equal("updateMRSupportedCommands", command.Type));
        Assert.DoesNotContain(fake.Commands, command => command.Type is { } type && RemovedPushes.Contains(type));

        // MediaRemote's own state is untouched: nothing we sent could paint a tile there.
        Assert.Null(fake.DisplayTitle);
        Assert.Null(fake.NowPlayingClient);
        Assert.Null(fake.PlaybackState);
        Assert.Empty(fake.Rejected);
    }

    /// <summary>
    /// The controls, which are the one thing MediaRemote IS used for on an announce.
    ///
    /// The exact list matters: a real iPhone advertises six transport commands for a live
    /// stream, and the fifteen-command list this used to send (shuffle, repeat, scrub, skip)
    /// is capabilities a receiver cannot reconcile with a realtime source - it greys the
    /// whole thing out. The empty push in front of it is part of the shape too.
    /// </summary>
    [Fact]
    public async Task Item_is_controllable_with_the_iphones_lean_six_commands()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());

        fake.WaitFor(() => fake.SupportedCommands.Count == 6, "the transport commands to arrive");

        // PreviousTrack, NextTrack, Stop, TogglePlayPause, Pause, Play - in that order.
        Assert.Equal(new long[] { 5, 4, 3, 2, 1, 0 }, fake.SupportedCommands);

        var pushes = fake.Commands.Where(command => command.Type == "updateMRSupportedCommands").ToList();
        Assert.True(pushes.Count >= 2, $"expected an empty push then a populated one, got {pushes.Count}");
        Assert.Empty(CommandIds(pushes[0]));
        Assert.Equal(6, CommandIds(pushes[1]).Count);

        Assert.Empty(fake.Rejected);
    }

    [Fact]
    public async Task Track_change_repaints_the_tile_without_losing_artwork()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("First Track", "OrgZ", "Conformance", TimeSpan.FromMinutes(3), Jpeg());
        fake.WaitFor(() => fake.Metadata.Any(m => m.Title == "First Track"), "the first track to paint");
        fake.WaitFor(() => fake.Artwork.Count > 0, "the first cover to arrive");

        var coversBefore = fake.Artwork.Count;

        await session.SetTrackInfoAsync("Second Track", "OrgZ", "Conformance", TimeSpan.FromMinutes(2), Jpeg());
        fake.WaitFor(() => fake.Metadata[^1].Title == "Second Track", "the track change to paint");
        fake.WaitFor(() => fake.Artwork.Count > coversBefore, "the second cover to arrive");

        // The cover goes out AGAIN on every track change: the receiver caches nothing by
        // identifier, so a change announced without one leaves the previous track's art up.
        Assert.Equal("image/jpeg", fake.Artwork[^1].ContentType);
        Assert.Equal(120000, fake.Metadata[^1].DurationMs);

        // Every announcement is ANCHORED. Text with no RTP-Info describes an item the
        // receiver cannot place on the timeline, and it shows nothing rather than guessing.
        Assert.All(fake.Metadata, update => Assert.NotNull(update.RtpTime));
        Assert.Empty(fake.Rejected);
    }

    /// <summary>
    /// A speaker in a real house is always holding somebody else's item when a new sender
    /// arrives. We announce over the DMAP path, which is scoped to our own stream - so the
    /// foreign MediaRemote tile is not something we touch, and certainly not something we
    /// half-overwrite with a "replace" push whose keys the receiver can't name.
    /// </summary>
    [Fact]
    public async Task Foreign_now_playing_is_left_alone_by_the_dmap_path()
    {
        using var fake = new FakeAirPlayReceiver();

        fake.SeedForeignNowPlaying("Someone Else's Song");

        using var session = await ConnectAndStreamAsync(fake);
        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());

        fake.WaitFor(() => fake.Metadata.Any(m => m.Title == "Golden Record"), "our item to reach the receiver");

        Assert.Equal("Voyager", fake.Metadata[^1].Artist);
        Assert.Equal("Someone Else's Song", fake.DisplayTitle);
        Assert.Equal("Someone Else", fake.DisplayArtist);
        Assert.Empty(fake.Rejected);
    }

    [Fact]
    public async Task Dmap_path_carries_the_same_track_in_reference_order()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());

        fake.WaitFor(() => fake.Metadata.Count > 0, "the DMAP text to arrive");
        fake.WaitFor(() => fake.Progress.Count > 0, "the progress triple to arrive");

        var metadata = fake.Metadata[^1];
        Assert.Equal("Golden Record", metadata.Title);
        Assert.Equal("Voyager", metadata.Artist);
        Assert.Equal("Sounds of Earth", metadata.Album);
        Assert.Equal(180000, metadata.DurationMs);

        // Reference order: progress first, then text, then artwork. The receiver anchors the
        // item to the progress triple, so text ahead of it describes an unplaced item.
        var requests = fake.Requests;
        var progressAt = IndexOf(requests, r => r.ContentType?.StartsWith("text/parameters") == true && r.Text.StartsWith("progress:"));
        var textAt = IndexOf(requests, r => r.ContentType?.StartsWith("application/x-dmap-tagged") == true);
        var artworkAt = IndexOf(requests, r => r.ContentType?.StartsWith("image/") == true);

        Assert.True(progressAt >= 0, "no progress was ever sent");
        Assert.True(textAt >= 0, "no DMAP text was ever sent");
        Assert.True(artworkAt >= 0, "no artwork was ever sent");
        Assert.True(progressAt < textAt, $"progress (#{progressAt}) must precede DMAP text (#{textAt})");
        Assert.True(textAt < artworkAt, $"DMAP text (#{textAt}) must precede artwork (#{artworkAt})");
    }

    /// <summary>
    /// The keep-alive, which is what turned "the tile paints sometimes" into "the tile
    /// paints". One announcement races the receiver's timeline lock and is dropped when it
    /// loses; re-asserting progress every second and the text every few is how the reference
    /// realtime sender wins that race every time.
    /// </summary>
    [Fact]
    public async Task Metadata_is_re_asserted_while_the_track_plays()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());
        fake.WaitFor(() => fake.Progress.Count > 0, "the first progress triple");

        var progressBefore = fake.Progress.Count;
        var textBefore = fake.Metadata.Count;

        await Task.Delay(TimeSpan.FromSeconds(3.5), CancellationToken.None);

        Assert.True(fake.Progress.Count >= progressBefore + 2, $"progress stopped: {progressBefore} -> {fake.Progress.Count}");
        Assert.True(fake.Metadata.Count > textBefore, $"the DMAP text was never re-asserted: {textBefore} -> {fake.Metadata.Count}");
        Assert.Empty(fake.Rejected);
    }

    [Fact]
    public async Task Hold_says_nothing_and_the_release_claims_the_display_as_one_playing_sequence()
    {
        using var fake = new FakeAirPlayReceiver();
        fake.SeedForeignNowPlaying("Someone Else's Song");

        using var session = new AirPlay2Session(fake.Host, fake.Port) { StartPaused = true };
        await session.ConnectAsync(CancellationToken.None);

        // The hold: silence flows, the track is seeded - and NOTHING goes on the wire. An
        // item announced by a paused client splits the opening sequence, which is the
        // documented way to a receiver that 200s everything and renders nothing.
        for (var i = 0; i < 20; i++)
        {
            await session.SendPacketAsync(Silence, CancellationToken.None);
        }

        await session.SetTrackInfoAsync("Held Track", "OrgZ", "Conformance", TimeSpan.FromMinutes(3), Jpeg());

        // Long enough that a keep-alive would have fired if the hold leaked one.
        await Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken.None);

        Assert.True(session.IsHolding, "the session should still be holding");
        Assert.True(session.IsPaused, "a holding session tells the receiver it is paused");
        Assert.Empty(fake.Metadata);
        Assert.Empty(fake.Progress);
        Assert.Empty(fake.SupportedCommands);
        Assert.Equal("Someone Else's Song", fake.DisplayTitle);   // untouched - we said nothing

        // The release: the receiver's FIRST word about this track is the whole sequence,
        // from a client that is playing.
        await session.NotifyPlaybackStartedAsync(CancellationToken.None);

        fake.WaitFor(() => fake.Metadata.Any(m => m.Title == "Held Track"), "the release to announce the track");
        fake.WaitFor(() => fake.SupportedCommands.Count == 6, "the release to offer the controls");
        fake.WaitFor(() => fake.Artwork.Count > 0, "the release to send the cover");

        Assert.False(session.IsPaused);
        Assert.False(session.IsHolding);
        Assert.True(fake.Progress.Count > 0, "the release must place the item on the timeline");
        Assert.Empty(fake.Rejected);
    }

    /// <summary>
    /// Pause and resume, which are the ONLY place the now-playing push survives: a
    /// timeline pin carrying nothing but the position and the rate, then the state change.
    /// Without the pin the receiver's tile goes on counting up through the pause, because it
    /// extrapolates from the last ElapsedTime and a rate we declared as 1.
    /// </summary>
    [Fact]
    public async Task Pause_pins_the_timeline_and_the_resume_says_playing()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        await session.SetTrackInfoAsync("Golden Record", "Voyager", "Sounds of Earth", TimeSpan.FromMinutes(3), Jpeg());
        fake.WaitFor(() => fake.Metadata.Count > 0, "the announcement to land");

        await session.FlushAsync(CancellationToken.None);
        fake.WaitFor(() => fake.PlaybackState == 2L, "the receiver to be told the stream paused");

        var pin = fake.Commands.Last(command => command.Type == "updateMRNowPlayingInfo");
        Assert.Equal("update", pin.Params?["mergePolicy"] as string);

        var timeline = Assert.IsType<Dictionary<string, object?>>(pin.Params?["params"]);
        Assert.Equal(
            new[] { NowPlayingKey + "DefaultPlaybackRate", NowPlayingKey + "ElapsedTime", NowPlayingKey + "PlaybackRate", NowPlayingKey + "Timestamp" },
            timeline.Keys.Order());
        Assert.Equal(0.0, Assert.IsType<double>(timeline[NowPlayingKey + "PlaybackRate"]));

        // Resuming is the first packet after the flush: it re-anchors the timeline, says
        // where the track really is, and only then says the stream is running again.
        await session.SendPacketAsync(Silence, CancellationToken.None);
        fake.WaitFor(() => fake.PlaybackState == 1L, "the receiver to be told the stream resumed");

        var resume = fake.Commands.Last(command => command.Type == "updateMRNowPlayingInfo");
        var resumed = Assert.IsType<Dictionary<string, object?>>(resume.Params?["params"]);
        Assert.Equal(1.0, Assert.IsType<double>(resumed[NowPlayingKey + "PlaybackRate"]));

        Assert.False(session.IsPaused);
        Assert.Empty(fake.Rejected);
    }

    // ── The transport under the metadata ────────────────────

    /// <summary>
    /// The audio itself, decrypted and decoded by the receiver rather than merely counted.
    /// A stream that seals with the wrong key, shifts the raw-ALAC frame, or stamps a
    /// different ssrc per packet is one the speaker silently drops - and every metadata
    /// assertion above would still pass.
    /// </summary>
    [Fact]
    public async Task Sealed_audio_decodes_back_to_the_pcm_that_was_sent()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        fake.WaitFor(() => fake.Audio.Count >= 10, "the audio packets to arrive");

        var packets = fake.Audio;
        Assert.All(packets, packet => Assert.Null(packet.Fault));
        Assert.All(packets, packet => Assert.Equal(Silence, packet.Pcm));
        Assert.Single(packets.Select(packet => packet.Ssrc).Distinct());

        // The first packet of a stream is MARKED - it is how a receiver tells a fresh stream
        // from the tail of a stale one.
        var first = packets.MinBy(packet => packet.Timestamp);
        Assert.NotNull(first);
        Assert.True(first!.Marker, "the first packet of the stream must carry the marker bit");
    }

    /// <summary>
    /// The clock exchange. A receiver asks before it will start a realtime stream at all, so
    /// an unanswered - or wrongly echoed - query is not a missing nicety, it is the stream
    /// never starting.
    /// </summary>
    [Fact]
    public async Task The_sender_answers_the_receivers_clock_queries()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        fake.WaitFor(() => fake.TimingRepliesAccepted > 0, "the sender to answer a clock query");
        Assert.Empty(fake.TimingFaults);
    }

    /// <summary>
    /// The goodbye iOS sends, in the order it sends it: one TEARDOWN naming the stream (the
    /// receiver stops it and KEEPS the connection), then one with an empty plist that ends
    /// the session. A receiver left holding a session nobody ended eventually stops answering
    /// anything, /info on a fresh connection included.
    /// </summary>
    [Fact]
    public async Task Disposing_says_goodbye_in_two_steps()
    {
        using var fake = new FakeAirPlayReceiver();
        var session = await ConnectAndStreamAsync(fake);

        session.Dispose();

        fake.WaitFor(() => fake.Teardowns.Count == 2, "both halves of the goodbye");
        Assert.Equal(new[] { "streams", "session" }, fake.Teardowns);
        Assert.True(fake.TornDown, "the session teardown must end the session");
    }

    /// <summary>
    /// Volume ADOPTION: the level someone set on the speaker wins over the one in our
    /// settings. Read first, then written back - the session still ends on a volume, because
    /// some receivers only register it as the final request of the start sequence.
    /// </summary>
    [Fact]
    public async Task Session_adopts_the_level_the_speaker_is_already_at()
    {
        using var fake = new FakeAirPlayReceiver { CurrentVolumeDb = -21.0 };
        using var session = new AirPlay2Session(fake.Host, fake.Port) { InitialVolume = 1f };
        await session.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, fake.VolumeReads);
        Assert.NotEmpty(fake.Volumes);
        Assert.Equal(-21.0, fake.Volumes[0], 3);
    }

    /// <summary>
    /// Plenty of receivers don't implement GET_PARAMETER at all. A sender that treats the
    /// refusal as an error opens silent; one that treats it as "no opinion" opens at the
    /// level the app asked for, which is what this does.
    /// </summary>
    [Fact]
    public async Task A_receiver_that_refuses_the_volume_read_gets_ours()
    {
        using var fake = new FakeAirPlayReceiver { AnswerVolumeReads = false };
        using var session = new AirPlay2Session(fake.Host, fake.Port) { InitialVolume = 0.25f };
        await session.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, fake.VolumeReads);
        Assert.NotEmpty(fake.Volumes);
        Assert.Equal(-22.5, fake.Volumes[0], 3);
    }

    /// <summary>
    /// The reverse event channel: the receiver's own buttons. The sender's REPLY matters as
    /// much as the action - a receiver that doesn't get one treats the sender as gone and
    /// fades the stream out after about half a minute.
    /// </summary>
    [Fact]
    public async Task Receiver_buttons_reach_the_sender_over_the_event_channel()
    {
        using var fake = new FakeAirPlayReceiver();
        using var session = await ConnectAndStreamAsync(fake);

        var commands = new List<string>();
        session.RemoteCommand += (_, command) =>
        {
            lock (commands)
            {
                commands.Add(command);
            }
        };

        fake.WaitFor(() => fake.EventChannelOpen, "the sender to open the event channel");

        var reply = fake.SendCommandEvent("nitm");
        Assert.StartsWith("RTSP/1.0 200", reply);

        fake.WaitFor(
            () =>
            {
                lock (commands)
                {
                    return commands.Contains("nitm");
                }
            },
            "the skip to reach the sender");
    }

    // ── The control endpoint the receiver calls back ────────

    [Theory]
    [InlineData("GET /ctrl-int/1/playpause HTTP/1.1\r\n\r\n", "playpause")]
    [InlineData("GET /ctrl-int/1/nextitem HTTP/1.1\r\n\r\n", "nextitem")]
    [InlineData("GET /ctrl-int/1/previtem HTTP/1.1\r\n\r\n", "previtem")]
    [InlineData("GET /ctrl-int/1/setproperty?dmcp.device-volume=-14.5 HTTP/1.1\r\n\r\n", "setproperty")]
    [InlineData("GET /ctrl-int/1/getproperty?properties=dmcp.volume HTTP/1.1\r\n\r\n", "getproperty")]
    [InlineData("GET /nothing/here HTTP/1.1\r\n\r\n", null)]
    public void Dacp_command_is_the_last_path_segment(string request, string? expected)
    {
        Assert.Equal(expected, DacpControlServer.ParseCommand(request));
    }

    [Theory]
    // The remote sends AirPlay's decibels; -144 is muted, and the scale is -30..0.
    [InlineData("GET /ctrl-int/1/setproperty?dmcp.device-volume=-14.5 HTTP/1.1\r\n\r\n", 0.5166)]
    [InlineData("GET /ctrl-int/1/setproperty?dmcp.device-volume=-144.0 HTTP/1.1\r\n\r\n", 0.0)]
    [InlineData("GET /ctrl-int/1/setproperty?dmcp.device-volume=0 HTTP/1.1\r\n\r\n", 1.0)]
    // And occasionally a plain percentage instead.
    [InlineData("GET /ctrl-int/1/setproperty?dmcp.volume=50 HTTP/1.1\r\n\r\n", 0.5)]
    public void Dacp_volume_is_read_in_the_invariant_culture(string request, double expected)
    {
        var level = DacpControlServer.ParseVolume(request);
        Assert.NotNull(level);
        Assert.Equal(expected, level!.Value, 3);
    }

    [Fact]
    public void Dacp_has_no_volume_to_read_in_a_plain_command()
    {
        Assert.Null(DacpControlServer.ParseVolume("GET /ctrl-int/1/playpause HTTP/1.1\r\n\r\n"));
    }

    /// <summary>
    /// The receiver's health check, on the socket it really uses. It KEEPS the connection and
    /// polls dmcp.volume about once a second; five bad answers is it declaring the sender
    /// uncontrollable, which the remote renders as "Controls Not Available". So both polls
    /// have to be answered, with a DMAP body whose length matches the header.
    /// </summary>
    [Fact]
    public void Dacp_answers_every_poll_on_one_connection()
    {
        using var fake = new FakeAirPlayReceiver();
        var dacp = DacpControlServer.Instance;
        dacp.ReportVolume(0.5f);

        var replies = fake.SendDacpSequence(dacp.Port,
            "/ctrl-int/1/getproperty?properties=dmcp.volume",
            "/ctrl-int/1/getproperty?properties=dmcp.volume",
            "/ctrl-int/1/playpause");

        Assert.Equal(3, replies.Count);

        foreach (var poll in replies.Take(2))
        {
            Assert.Equal(200, poll.Status);
            Assert.Equal("application/x-dmap-tagged", poll.Header("Content-Type"));
            Assert.Equal(poll.Body.Length.ToString(), poll.Header("Content-Length"));

            var dmap = DmapReader.Parse(poll.Body);
            Assert.Equal("cmgt", dmap.Tags[0]);
            Assert.Equal(200, dmap.Number("mstt"));
            Assert.Equal(50, dmap.Number("cmvo"));
        }

        // A verb is acknowledged, not answered - an empty 200 counts against us.
        Assert.Equal(204, replies[2].Status);
        Assert.Empty(replies[2].Body);
    }

    [Fact]
    public void Dacp_names_the_speaker_it_is_driving()
    {
        using var fake = new FakeAirPlayReceiver();
        var dacp = DacpControlServer.Instance;

        var reply = fake.SendDacp(dacp.Port, "/ctrl-int/1/getspeakers");

        Assert.Equal(200, reply.Status);
        var dmap = DmapReader.Parse(reply.Body);
        Assert.Equal("casp", dmap.Tags[0]);
        Assert.True(dmap.Has("mdcl"), "a speaker list with no speaker in it");
        Assert.Equal("OrgZ", dmap.Text("minm"));
    }

    // ── Cover art ──────────────────────────────────────────

    /// <summary>
    /// A cover already inside the ceiling is left exactly as it is - re-encoding it would
    /// cost a decode per track change and buy nothing.
    /// </summary>
    [Fact]
    public void Artwork_leaves_a_small_jpeg_alone()
    {
        var jpeg = Jpeg();

        Assert.Same(jpeg, AirPlayArtwork.Fit(jpeg));
    }

    /// <summary>
    /// A PNG always leaves as JPEG. The MediaRemote push carries JPEG only - a real iPhone
    /// sends nothing else - so PNG art rides the DMAP path and then never displays.
    /// </summary>
    [Fact]
    public void Artwork_transcodes_a_png_cover_to_jpeg()
    {
        var png = ImageDecoder.EnsureRasterBytes(System.Text.Encoding.UTF8.GetBytes(SquareSvg), 1500);
        Assert.Equal(0x89, (int)png[0]);   // the fixture really did rasterize

        var fitted = AirPlayArtwork.Fit(png);

        Assert.Equal("image/jpeg", AirPlay2Session.ImageContentType(fitted));
        Assert.True(fitted.Length <= 45_000, $"a fitted cover is {fitted.Length}B, past what a receiver renders");
    }

    /// <summary>Bytes that are not an image at all come back untouched - art never throws.</summary>
    [Fact]
    public void Artwork_that_cannot_be_decoded_is_passed_through()
    {
        var junk = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };

        Assert.Same(junk, AirPlayArtwork.Fit(junk));
    }

    private const string SquareSvg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" fill="#036"/><circle cx="16" cy="16" r="10" fill="#fc0"/></svg>""";

    /// <summary>The MediaRemote command ids carried by one supported-commands push.</summary>
    private static List<long> CommandIds(FakeAirPlayReceiver.CommandMessage push)
    {
        var ids = new List<long>();
        if (push.Params?.TryGetValue("mrSupportedCommandsFromSender", out var list) != true || list is not List<object?> entries)
        {
            return ids;
        }

        foreach (var entry in entries)
        {
            if (entry is byte[] blob && BinaryPlist.Read(blob) is Dictionary<string, object?> command
                && command.TryGetValue("kCommandInfoCommandKey", out var id) && id is long number)
            {
                ids.Add(number);
            }
        }

        return ids;
    }

    private static int IndexOf(IReadOnlyList<FakeAirPlayReceiver.Request> requests, Func<FakeAirPlayReceiver.Request, bool> match)
    {
        for (var i = 0; i < requests.Count; i++)
        {
            if (match(requests[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The smallest thing that parses as a JPEG, distinct enough to assert on.</summary>
    private static byte[] Jpeg()
    {
        var jpeg = new byte[64];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        jpeg[3] = 0xE0;
        for (var i = 4; i < 62; i++)
        {
            jpeg[i] = (byte)i;
        }
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        return jpeg;
    }
}
