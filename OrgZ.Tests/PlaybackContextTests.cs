// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

public class PlaybackContextTests
{
    // -- Construction --

    [Fact]
    public void Constructor_StartItemInList_SetsCurrentToThatItem()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[2]);

        Assert.Equal(list[2], ctx.CurrentItem);
        Assert.Equal(2, ctx.CurrentIndex);
        Assert.True(ctx.CurrentItem.IsPlaying);
    }

    [Fact]
    public void Constructor_StartItemNotInList_PrependsAndPlaysIt()
    {
        var list = MakeList(3);
        var orphan = Music("orphan");

        var ctx = new PlaybackContext(list, orphan);

        Assert.Equal(orphan, ctx.CurrentItem);
        Assert.Equal(0, ctx.CurrentIndex);
        Assert.Equal(4, ctx.Playlist.Count);
        Assert.True(orphan.IsPlaying);
    }

    [Fact]
    public void Constructor_OnlyCurrentItemIsPlaying()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);

        Assert.True(list[0].IsPlaying);
        for (int i = 1; i < list.Count; i++)
        {
            Assert.False(list[i].IsPlaying);
        }
    }

    // -- MoveNext / MovePrevious --

    [Fact]
    public void MoveNext_AdvancesAndUpdatesIsPlaying()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);

        var next = ctx.MoveNext();

        Assert.Equal(list[1], next);
        Assert.True(list[1].IsPlaying);
        Assert.False(list[0].IsPlaying);
    }

    [Fact]
    public void MoveNext_AtEndWithRepeatOff_ReturnsNull()
    {
        var list = MakeList(2);
        var ctx = new PlaybackContext(list, list[1]);

        Assert.Null(ctx.MoveNext());
    }

    [Fact]
    public void MoveNext_AtEndWithRepeatAll_WrapsToFirst()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[2]) { RepeatMode = RepeatMode.All };

        var next = ctx.MoveNext();

        Assert.Equal(list[0], next);
        Assert.Equal(0, ctx.CurrentIndex);
    }

    [Fact]
    public void MoveNext_WithRepeatOne_ReturnsSameItem()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[1]) { RepeatMode = RepeatMode.One };

        var next = ctx.MoveNext();

        Assert.Equal(list[1], next);
        Assert.Equal(1, ctx.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_AtStartWithRepeatOff_ReturnsNull()
    {
        var list = MakeList(2);
        var ctx = new PlaybackContext(list, list[0]);

        Assert.Null(ctx.MovePrevious());
    }

    [Fact]
    public void MovePrevious_AtStartWithRepeatAll_WrapsToLast()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]) { RepeatMode = RepeatMode.All };

        var prev = ctx.MovePrevious();

        Assert.Equal(list[2], prev);
        Assert.Equal(2, ctx.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_NormalCase_GoesBack()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[2]);

        var prev = ctx.MovePrevious();

        Assert.Equal(list[1], prev);
        Assert.True(list[1].IsPlaying);
        Assert.False(list[2].IsPlaying);
    }

    // -- HasNext / HasPrevious --

    [Theory]
    [InlineData(RepeatMode.Off, 0, 3, true)]
    [InlineData(RepeatMode.Off, 2, 3, false)]
    [InlineData(RepeatMode.All, 2, 3, true)]
    [InlineData(RepeatMode.One, 2, 3, true)]
    public void HasNext_ReflectsRepeatMode(RepeatMode mode, int startIdx, int listSize, bool expected)
    {
        var list = MakeList(listSize);
        var ctx = new PlaybackContext(list, list[startIdx]) { RepeatMode = mode };
        Assert.Equal(expected, ctx.HasNext);
    }

    [Theory]
    [InlineData(RepeatMode.Off, 0, 3, false)]
    [InlineData(RepeatMode.Off, 1, 3, true)]
    [InlineData(RepeatMode.All, 0, 3, true)]
    public void HasPrevious_ReflectsRepeatMode(RepeatMode mode, int startIdx, int listSize, bool expected)
    {
        var list = MakeList(listSize);
        var ctx = new PlaybackContext(list, list[startIdx]) { RepeatMode = mode };
        Assert.Equal(expected, ctx.HasPrevious);
    }

    // -- Contains / JumpTo --

    [Fact]
    public void Contains_ReturnsTrueForListItem()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);

        Assert.True(ctx.Contains(list[2]));
        Assert.False(ctx.Contains(Music("not-in-list")));
    }

    [Fact]
    public void JumpTo_ItemInList_MovesAndUpdatesIsPlaying()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);

        var ok = ctx.JumpTo(list[3]);

        Assert.True(ok);
        Assert.Equal(list[3], ctx.CurrentItem);
        Assert.True(list[3].IsPlaying);
        Assert.False(list[0].IsPlaying);
    }

    [Fact]
    public void JumpTo_ItemNotInList_ReturnsFalse()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);

        var ok = ctx.JumpTo(Music("not-here"));

        Assert.False(ok);
        Assert.Equal(list[0], ctx.CurrentItem);
    }

    // -- InsertNext / Append / Queue ops --

    [Fact]
    public void InsertNext_PlacesItemImmediatelyAfterCurrent()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);
        var newItem = Music("inserted");

        ctx.InsertNext(newItem);

        Assert.Equal(newItem, ctx.Playlist[1]);
        Assert.Equal(newItem, ctx.UpcomingItems[0]);
    }

    [Fact]
    public void InsertNext_ThenMoveNext_PlaysInserted()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);
        var newItem = Music("inserted");

        ctx.InsertNext(newItem);
        var next = ctx.MoveNext();

        Assert.Equal(newItem, next);
    }

    [Fact]
    public void Append_AddsToEnd()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);
        var added = Music("appended");

        ctx.Append(added);

        Assert.Equal(added, ctx.Playlist[^1]);
        Assert.Equal(added, ctx.UpcomingItems[^1]);
    }

    [Fact]
    public void RemoveFromUpcoming_RemovesItemAndShrinksPlaylist()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);
        var originalCount = ctx.Playlist.Count;
        var doomedItem = ctx.UpcomingItems[1]; // playlist[2]

        ctx.RemoveFromUpcoming(1);

        Assert.Equal(originalCount - 1, ctx.Playlist.Count);
        Assert.DoesNotContain(doomedItem, ctx.Playlist);
        Assert.DoesNotContain(doomedItem, ctx.UpcomingItems);
    }

    [Fact]
    public void RemoveFromUpcoming_OutOfRange_NoOp()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[0]);
        var originalCount = ctx.Playlist.Count;

        ctx.RemoveFromUpcoming(99);
        ctx.RemoveFromUpcoming(-1);

        Assert.Equal(originalCount, ctx.Playlist.Count);
    }

    [Fact]
    public void MoveInUpcoming_ReordersItem()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);
        // Upcoming = [list[1], list[2], list[3], list[4]]
        var first = ctx.UpcomingItems[0];

        ctx.MoveInUpcoming(0, 2);

        // first should now be at index 2 of upcoming
        Assert.Equal(first, ctx.UpcomingItems[2]);
    }

    [Fact]
    public void ClearUpcoming_LeavesOnlyCurrentBehind()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);

        ctx.ClearUpcoming();

        Assert.Single(ctx.Playlist);
        Assert.Empty(ctx.UpcomingItems);
        Assert.Equal(list[0], ctx.CurrentItem);
    }

    [Fact]
    public void ClearUpcoming_WhenAlreadyAtEnd_NoOp()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[2]);

        ctx.ClearUpcoming();

        Assert.Equal(3, ctx.Playlist.Count);
        Assert.Empty(ctx.UpcomingItems);
    }

    // -- Shuffle --

    [Fact]
    public void Constructor_WithShuffle_PinsCurrentToIndexZero()
    {
        var list = MakeList(20);
        var pickedItem = list[7];

        var ctx = new PlaybackContext(list, pickedItem, shuffle: true);

        Assert.True(ctx.IsShuffled);
        Assert.Equal(0, ctx.CurrentIndex);
        Assert.Equal(pickedItem, ctx.CurrentItem);
        Assert.Equal(20, ctx.Playlist.Count);
    }

    [Fact]
    public void SetShuffle_OnAfterOff_RebuildsAndPinsCurrent()
    {
        var list = MakeList(15);
        var ctx = new PlaybackContext(list, list[5]);

        ctx.SetShuffle(true);

        Assert.True(ctx.IsShuffled);
        Assert.Equal(list[5], ctx.CurrentItem);
        Assert.Equal(0, ctx.CurrentIndex);
        Assert.Equal(15, ctx.Playlist.Count);
    }

    [Fact]
    public void SetShuffle_OffAfterOn_RestoresOriginalOrder()
    {
        var list = MakeList(10);
        var ctx = new PlaybackContext(list, list[3], shuffle: true);

        ctx.SetShuffle(false);

        Assert.False(ctx.IsShuffled);
        // Original list order restored
        for (int i = 0; i < list.Count; i++)
        {
            Assert.Equal(list[i], ctx.Playlist[i]);
        }
        Assert.Equal(list[3], ctx.CurrentItem);
        Assert.Equal(3, ctx.CurrentIndex);
    }

    [Fact]
    public void SetShuffle_NoOpWhenAlreadyMatching()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[2]);

        ctx.SetShuffle(false); // already off
        Assert.False(ctx.IsShuffled);

        ctx.SetShuffle(true);
        ctx.SetShuffle(true); // calling on twice should be idempotent
        Assert.True(ctx.IsShuffled);
        Assert.Equal(list[2], ctx.CurrentItem);
    }

    // -- Release --

    [Fact]
    public void Release_ClearsCurrentIsPlaying()
    {
        var list = MakeList(3);
        var ctx = new PlaybackContext(list, list[1]);
        Assert.True(list[1].IsPlaying);

        ctx.Release();

        Assert.False(list[1].IsPlaying);
    }

    // -- IsPlaying ownership invariant --

    [Fact]
    public void OnlyOneItemIsPlayingAtAnyTime()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);

        AssertExactlyOnePlaying(list);
        ctx.MoveNext();
        AssertExactlyOnePlaying(list);
        ctx.MoveNext();
        AssertExactlyOnePlaying(list);
        ctx.MovePrevious();
        AssertExactlyOnePlaying(list);
        ctx.JumpTo(list[4]);
        AssertExactlyOnePlaying(list);
    }

    private static void AssertExactlyOnePlaying(List<MediaItem> items)
    {
        var playing = items.Count(i => i.IsPlaying);
        Assert.Equal(1, playing);
    }

    // -- Shuffle mid-playback --

    [Fact]
    public void SetShuffle_OnMidPlayback_PinsCurrentAndPreservesAllTracks()
    {
        var list = MakeList(10);
        var ctx = new PlaybackContext(list, list[0]);
        ctx.MoveNext(); // now at index 1
        ctx.MoveNext(); // now at index 2

        var currentBefore = ctx.CurrentItem;

        ctx.SetShuffle(true);

        Assert.Equal(currentBefore, ctx.CurrentItem);
        Assert.Equal(0, ctx.CurrentIndex);
        Assert.Equal(10, ctx.Playlist.Count);
        Assert.True(ctx.IsShuffled);
    }

    [Fact]
    public void SetShuffle_OffMidPlayback_RestoresOriginalOrderAndFindsCurrentPosition()
    {
        var list = MakeList(10);
        var ctx = new PlaybackContext(list, list[3], shuffle: true);

        // Advance a few in shuffled order
        ctx.MoveNext();
        ctx.MoveNext();
        var currentItem = ctx.CurrentItem;

        ctx.SetShuffle(false);

        // Current item should still be the same track
        Assert.Equal(currentItem, ctx.CurrentItem);
        Assert.False(ctx.IsShuffled);
        // Original order restored
        for (int i = 0; i < list.Count; i++)
        {
            Assert.Equal(list[i], ctx.Playlist[i]);
        }
        // CurrentIndex should point to the right position in the original order
        Assert.Equal(list.IndexOf(currentItem), ctx.CurrentIndex);
    }

    [Fact]
    public void SetShuffle_OnThenOff_FullCycle_NoTracksLost()
    {
        var list = MakeList(20);
        var ctx = new PlaybackContext(list, list[5]);

        ctx.SetShuffle(true);
        ctx.MoveNext();
        ctx.MoveNext();
        ctx.MoveNext();

        ctx.SetShuffle(false);

        // Every original track must still be present
        foreach (var item in list)
        {
            Assert.Contains(item, ctx.Playlist);
        }
        Assert.Equal(20, ctx.Playlist.Count);
    }

    // -- Upcoming list maintenance --

    [Fact]
    public void UpcomingItems_RebuildsOnIndexChange()
    {
        var list = MakeList(5);
        var ctx = new PlaybackContext(list, list[0]);

        Assert.Equal(4, ctx.UpcomingItems.Count);

        ctx.MoveNext();
        Assert.Equal(3, ctx.UpcomingItems.Count);
        Assert.Equal(list[2], ctx.UpcomingItems[0]);

        ctx.JumpTo(list[4]);
        Assert.Empty(ctx.UpcomingItems);
    }
}
