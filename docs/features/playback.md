# Playback

OrgZ uses LibVLC for audio playback, supporting a wide range of formats and streaming protocols.

## Controls

- **Play/Pause** — Toggle playback
- **Previous/Next** — Navigate within the current playback context
- **Volume** — Slider with mute and max buttons
- **Seek Bar** — Click or drag to seek within a track (disabled for live streams)

## Playback Context

When you start playing a track, OrgZ captures a snapshot of the current filtered list as your **playback context**. This means:

- Auto-advance plays the next track from where you started
- Previous/Next navigate within that same list
- Switching views doesn't interrupt playback
- Starting a track from a different view creates a new context

## Live Streams

Radio streams show a "LIVE" duration and an animated progress indicator instead of a seek bar. Live ICY metadata updates the track info display in real time.

## Navigate to Playing

Click the album art or track info area at the top to jump to the currently playing item in its source view.

## Per-Track Options

OrgZ supports per-track volume adjustment, equalizer presets, and start/stop time markers (configured via the track info dialog).
