// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// The iTunes row tick that replaced the Ignored view: ticked means "take part" -
/// play through and sync - unticked means visible but skipped. Adversarial cases cover
/// an entirely-unticked list (must end, not spin), repeat modes, and the legacy
/// storage inversion.
/// </summary>
public class CheckedTrackTests
{
    private static MediaItem Track(string id, bool ticked = true)
        => new() { Id = id, Kind = MediaKind.Music, Title = id, IsChecked = ticked };

    // ── The flag itself ───────────────────────────────────────

    [Fact]
    public void Tracks_are_checked_by_default()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music };

        Assert.True(item.IsChecked);
        Assert.False(item.IsIgnored);
    }

    [Fact]
    public void Checked_is_the_inverse_of_the_legacy_stored_flag()
    {
        var item = Track("a");

        item.IsChecked = false;
        Assert.True(item.IsIgnored);      // what persists

        item.IsIgnored = false;
        Assert.True(item.IsChecked);      // and back
    }

    [Fact]
    public void Setting_the_stored_flag_notifies_the_ui_facing_property()
    {
        var item = Track("a");
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsChecked = false;

        Assert.Contains(nameof(MediaItem.IsChecked), raised);
        Assert.Contains(nameof(MediaItem.IsIgnored), raised);
    }

    // ── Play-through skipping ─────────────────────────────────

    [Fact]
    public void Play_through_skips_unticked_tracks()
    {
        var a = Track("a"); var b = Track("b", ticked: false); var c = Track("c");
        var ctx = new PlaybackContext([a, b, c], a);

        Assert.Same(c, ctx.MoveNext());   // b was skipped
    }

    [Fact]
    public void Consecutive_unticked_tracks_are_all_skipped()
    {
        var a = Track("a");
        var b = Track("b", ticked: false);
        var c = Track("c", ticked: false);
        var d = Track("d");
        var ctx = new PlaybackContext([a, b, c, d], a);

        Assert.Same(d, ctx.MoveNext());
    }

    [Fact]
    public void Trailing_unticked_tracks_end_the_list_rather_than_playing()
    {
        var a = Track("a"); var b = Track("b", ticked: false);
        var ctx = new PlaybackContext([a, b], a);

        Assert.Null(ctx.MoveNext());
    }

    [Fact]
    public void An_entirely_unticked_list_terminates_instead_of_spinning_forever()
    {
        // Adversarial: with RepeatAll and nothing ticked, a naive skip loop never exits.
        var a = Track("a", ticked: false);
        var b = Track("b", ticked: false);
        var ctx = new PlaybackContext([a, b], a) { RepeatMode = RepeatMode.All };

        Assert.Null(ctx.MoveNext());
    }

    [Fact]
    public void Repeat_all_wraps_past_unticked_tracks_to_a_ticked_one()
    {
        var a = Track("a");
        var b = Track("b", ticked: false);
        var ctx = new PlaybackContext([a, b], a) { RepeatMode = RepeatMode.All };

        Assert.Same(a, ctx.MoveNext());   // wrapped around b, back to a
    }

    [Fact]
    public void Repeat_one_ignores_the_tick_entirely()
    {
        // The user explicitly asked for this track on loop; the tick governs
        // play-THROUGH, not an explicit choice.
        var a = Track("a", ticked: false);
        var ctx = new PlaybackContext([a], a) { RepeatMode = RepeatMode.One };

        Assert.Same(a, ctx.MoveNext());
    }

    [Fact]
    public void An_all_ticked_list_behaves_exactly_as_before()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c");
        var ctx = new PlaybackContext([a, b, c], a);

        Assert.Same(b, ctx.MoveNext());
        Assert.Same(c, ctx.MoveNext());
        Assert.Null(ctx.MoveNext());
    }
}
