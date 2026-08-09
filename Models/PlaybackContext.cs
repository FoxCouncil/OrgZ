// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public partial class PlaybackContext : ObservableObject
{
    private readonly List<MediaItem> _originalOrder;
    private List<MediaItem> _playOrder;

    public RepeatMode RepeatMode { get; set; } = RepeatMode.Off;

    public IReadOnlyList<MediaItem> Playlist => _playOrder;

    public int CurrentIndex { get; private set; }

    public MediaItem CurrentItem => _playOrder[CurrentIndex];

    public bool HasNext
    {
        get
        {
            if (RepeatMode == RepeatMode.One || RepeatMode == RepeatMode.All)
            {
                return _playOrder.Count > 0;
            }

            return CurrentIndex < _playOrder.Count - 1;
        }
    }

    public bool HasPrevious
    {
        get
        {
            if (RepeatMode == RepeatMode.All)
            {
                return _playOrder.Count > 0;
            }

            return CurrentIndex > 0;
        }
    }

    public bool IsShuffled { get; private set; }

    public ShuffleBy ShuffleBy { get; private set; } = ShuffleBy.Song;

    [ObservableProperty]
    private ObservableCollection<MediaItem> _upcomingItems = [];

    public PlaybackContext(List<MediaItem> sourceList, MediaItem startItem, bool shuffle = false, ShuffleBy shuffleBy = ShuffleBy.Song)
    {
        _originalOrder = new List<MediaItem>(sourceList);
        _playOrder = new List<MediaItem>(sourceList);
        CurrentIndex = _playOrder.IndexOf(startItem);

        if (CurrentIndex < 0)
        {
            _playOrder.Insert(0, startItem);
            _originalOrder.Insert(0, startItem);
            CurrentIndex = 0;
        }

        CurrentItem.IsPlaying = true;

        if (shuffle)
        {
            ApplyShuffle(shuffleBy);
        }

        RebuildUpcoming();
    }

    public MediaItem? MoveNext()
    {
        if (_playOrder.Count == 0)
        {
            return null;
        }

        if (RepeatMode == RepeatMode.One)
        {
            return CurrentItem;
        }

        // Unticked tracks are skipped when the list plays THROUGH (iTunes' rule) - the
        // user can still start one directly. Bounded by the list length so an entirely
        // unticked list ends rather than spinning.
        for (int skipped = 0; skipped < _playOrder.Count; skipped++)
        {
            if (CurrentIndex < _playOrder.Count - 1)
            {
                SetCurrentIndex(CurrentIndex + 1);
            }
            else if (RepeatMode == RepeatMode.All)
            {
                SetCurrentIndex(0);
            }
            else
            {
                return null;
            }

            if (CurrentItem?.IsChecked != false)
            {
                return CurrentItem;
            }
        }

        return null;   // nothing ticked anywhere in the list
    }

    public MediaItem? MovePrevious()
    {
        if (_playOrder.Count == 0)
        {
            return null;
        }

        if (CurrentIndex > 0)
        {
            SetCurrentIndex(CurrentIndex - 1);
            return CurrentItem;
        }

        if (RepeatMode == RepeatMode.All)
        {
            SetCurrentIndex(_playOrder.Count - 1);
            return CurrentItem;
        }

        return null;
    }

    public void Release()
    {
        CurrentItem.IsPlaying = false;
    }

    public bool Contains(MediaItem item)
    {
        return _playOrder.Contains(item);
    }

    /// <summary>
    /// True when the source list this context was built from is the same
    /// sequence the caller is now offering. Used to decide whether playing a
    /// track from the current view can reuse the existing playback queue (and
    /// keep its shuffle order intact) or has to rebuild because the view's
    /// filter -- e.g. a search -- changed the candidate set.
    /// </summary>
    public bool MatchesSource(IReadOnlyList<MediaItem> source)
    {
        if (source.Count != _originalOrder.Count)
        {
            return false;
        }

        for (int i = 0; i < source.Count; i++)
        {
            if (!ReferenceEquals(source[i], _originalOrder[i]))
            {
                return false;
            }
        }

        return true;
    }

    public bool JumpTo(MediaItem item)
    {
        var idx = _playOrder.IndexOf(item);
        if (idx < 0)
        {
            return false;
        }

        SetCurrentIndex(idx);
        return true;
    }

    public void InsertNext(MediaItem item)
    {
        var insertIndex = CurrentIndex + 1;
        _playOrder.Insert(insertIndex, item);
        _originalOrder.Add(item);
        RebuildUpcoming();
    }

    public void Append(MediaItem item)
    {
        _playOrder.Add(item);
        _originalOrder.Add(item);
        RebuildUpcoming();
    }

    public void RemoveFromUpcoming(int upcomingIndex)
    {
        if (upcomingIndex < 0 || upcomingIndex >= UpcomingItems.Count)
        {
            return;
        }

        var item = UpcomingItems[upcomingIndex];
        var playIndex = _playOrder.IndexOf(item, CurrentIndex + 1);
        if (playIndex >= 0)
        {
            _playOrder.RemoveAt(playIndex);
        }

        RebuildUpcoming();
    }

    public void MoveInUpcoming(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= UpcomingItems.Count || toIndex < 0 || toIndex >= UpcomingItems.Count)
        {
            return;
        }

        int baseOffset = CurrentIndex + 1;
        var item = _playOrder[baseOffset + fromIndex];
        _playOrder.RemoveAt(baseOffset + fromIndex);
        _playOrder.Insert(baseOffset + toIndex, item);
        RebuildUpcoming();
    }

    public void ClearUpcoming()
    {
        if (CurrentIndex < _playOrder.Count - 1)
        {
            _playOrder.RemoveRange(CurrentIndex + 1, _playOrder.Count - CurrentIndex - 1);
        }

        RebuildUpcoming();
    }

    public void SetShuffle(bool enabled, ShuffleBy by = ShuffleBy.Song)
    {
        if (enabled && (!IsShuffled || by != ShuffleBy))
        {
            ApplyShuffle(by);
        }
        else if (!enabled && IsShuffled)
        {
            RemoveShuffle();
        }
    }

    private void SetCurrentIndex(int newIndex)
    {
        CurrentItem.IsPlaying = false;
        CurrentIndex = newIndex;
        CurrentItem.IsPlaying = true;
        RebuildUpcoming();
    }

    private void ApplyShuffle(ShuffleBy by)
    {
        var current = CurrentItem;

        _playOrder = by == ShuffleBy.Album ? AlbumShuffleOrder(current) : SongShuffleOrder(current);
        CurrentIndex = _playOrder.IndexOf(current);
        IsShuffled = true;
        ShuffleBy = by;
        RebuildUpcoming();
    }

    private List<MediaItem> SongShuffleOrder(MediaItem current)
    {
        var rng = Random.Shared;

        var order = new List<MediaItem>(_originalOrder);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        order.Remove(current);
        order.Insert(0, current);
        return order;
    }

    /// <summary>
    /// Album shuffle: albums land in random order, each keeping its tracks in source-list
    /// order, and the current track's album leads so playback continues through it (its
    /// earlier tracks stay behind the current one, so Previous walks back through the album).
    /// Untagged tracks shuffle individually. Grouping is by album name alone - MediaItem has
    /// no album-artist tag, and merging two same-named albums is the lesser evil next to
    /// splitting every various-artists compilation per track.
    /// </summary>
    private List<MediaItem> AlbumShuffleOrder(MediaItem current)
    {
        var groups = new List<List<MediaItem>>();
        var byAlbum = new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _originalOrder)
        {
            if (string.IsNullOrWhiteSpace(item.Album))
            {
                groups.Add([item]);
                continue;
            }

            if (!byAlbum.TryGetValue(item.Album, out var group))
            {
                group = [];
                byAlbum[item.Album] = group;
                groups.Add(group);
            }

            group.Add(item);
        }

        var rng = Random.Shared;
        for (int i = groups.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (groups[i], groups[j]) = (groups[j], groups[i]);
        }

        var currentGroup = groups.First(g => g.Contains(current));
        groups.Remove(currentGroup);
        groups.Insert(0, currentGroup);

        var order = new List<MediaItem>(_originalOrder.Count);
        foreach (var group in groups)
        {
            order.AddRange(group);
        }

        return order;
    }

    private void RemoveShuffle()
    {
        var current = CurrentItem;
        _playOrder = new List<MediaItem>(_originalOrder);
        CurrentIndex = _playOrder.IndexOf(current);

        if (CurrentIndex < 0)
        {
            CurrentIndex = 0;
        }

        IsShuffled = false;
        RebuildUpcoming();
    }

    private void RebuildUpcoming()
    {
        var start = CurrentIndex + 1;
        var wanted = Math.Max(0, _playOrder.Count - start);

        // Already correct - every caller funnels through here, and most calls don't
        // actually change the tail.
        if (wanted == UpcomingItems.Count && SuffixMatches(0))
        {
            return;
        }

        // Advancing a track only drops items off the FRONT; everything after is the
        // same object in the same order. Removing those few beats clearing and
        // re-adding the remainder, which fired one CollectionChanged per row - on a
        // shuffled 10k-track queue that was ~10k UI notifications at every song end.
        if (wanted < UpcomingItems.Count && SuffixMatches(UpcomingItems.Count - wanted))
        {
            for (var i = UpcomingItems.Count - wanted; i > 0; i--)
            {
                UpcomingItems.RemoveAt(0);
            }
            return;
        }

        UpcomingItems.Clear();
        for (var i = start; i < _playOrder.Count; i++)
        {
            UpcomingItems.Add(_playOrder[i]);
        }

        // Whether the last `wanted` entries of UpcomingItems (starting at `offset`)
        // already are the play order's tail.
        bool SuffixMatches(int offset)
        {
            for (var i = 0; i < wanted; i++)
            {
                if (!ReferenceEquals(UpcomingItems[offset + i], _playOrder[start + i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
