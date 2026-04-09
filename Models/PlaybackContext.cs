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

    [ObservableProperty]
    private ObservableCollection<MediaItem> _upcomingItems = [];

    public PlaybackContext(List<MediaItem> sourceList, MediaItem startItem, bool shuffle = false)
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
            ApplyShuffle();
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

        if (CurrentIndex < _playOrder.Count - 1)
        {
            SetCurrentIndex(CurrentIndex + 1);
            return CurrentItem;
        }

        if (RepeatMode == RepeatMode.All)
        {
            SetCurrentIndex(0);
            return CurrentItem;
        }

        return null;
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

    public void SetShuffle(bool enabled)
    {
        if (enabled && !IsShuffled)
        {
            ApplyShuffle();
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

    private void ApplyShuffle()
    {
        var current = CurrentItem;
        var rng = Random.Shared;

        _playOrder = new List<MediaItem>(_originalOrder);
        for (int i = _playOrder.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_playOrder[i], _playOrder[j]) = (_playOrder[j], _playOrder[i]);
        }

        _playOrder.Remove(current);
        _playOrder.Insert(0, current);
        CurrentIndex = 0;
        IsShuffled = true;
        RebuildUpcoming();
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
        UpcomingItems.Clear();
        for (int i = CurrentIndex + 1; i < _playOrder.Count; i++)
        {
            UpcomingItems.Add(_playOrder[i]);
        }
    }
}
