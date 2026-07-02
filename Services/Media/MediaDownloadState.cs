// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.Media;

/// <summary>
/// Whether an acquired item's bytes are on disk and usable - the one state shared by every
/// downloadable vertical (podcast episodes, audiobooks). The filesystem is the authoritative
/// registry: this is always computed by probing disk + in-flight jobs, never stored, so a stale
/// row can never disagree with the file that actually exists. "Downloaded" means a complete,
/// playable file is present; "Incomplete" is the interrupted / wrong-size case a re-download fixes.
/// </summary>
public enum MediaDownloadState
{
    NotDownloaded,
    InProgress,
    Downloaded,
    Incomplete,
}
