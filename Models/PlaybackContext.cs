// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public class PlaybackContext
{
    public List<MediaItem> Playlist { get; }

    public int CurrentIndex { get; private set; }

    public MediaItem CurrentItem => Playlist[CurrentIndex];

    public bool HasNext => CurrentIndex < Playlist.Count - 1;

    public bool HasPrevious => CurrentIndex > 0;

    public PlaybackContext(List<MediaItem> sourceList, MediaItem startItem)
    {
        Playlist = new List<MediaItem>(sourceList);
        CurrentIndex = Playlist.IndexOf(startItem);

        if (CurrentIndex < 0)
        {
            Playlist.Insert(0, startItem);
            CurrentIndex = 0;
        }
    }

    public MediaItem? MoveNext()
    {
        if (!HasNext)
        {
            return null;
        }

        CurrentIndex++;
        return CurrentItem;
    }

    public MediaItem? MovePrevious()
    {
        if (!HasPrevious)
        {
            return null;
        }

        CurrentIndex--;
        return CurrentItem;
    }
}
