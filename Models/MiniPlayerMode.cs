// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public enum MiniPlayerMode
{
    /// <summary>
    /// iTunes-style: opening the mini-player hides the main window.  Closing the
    /// mini-player or clicking its expand button brings the main window back.
    /// </summary>
    Replace,

    /// <summary>
    /// Apple Music / Spotify-style: the main window stays visible alongside the
    /// mini-player.  Useful on multi-monitor setups where the mini-player lives
    /// permanently on a secondary display.
    /// </summary>
    SideBySide,
}
