# Supported Hardware

OrgZ is developed and validated against real devices - every iPod generation has
its own database format, checksum scheme, and codec support, and only real
hardware proves a sync path. This page lists what OrgZ supports and what your
files are converted to on the way in.

## iPods

**Sync tier** is the on-device database OrgZ writes for that generation.
**Transcode** is the codec (as its fourCC) OrgZ converts to when a source file
isn't natively playable on that model - `alac` is Apple Lossless (lossless),
`mp4a` is AAC at 256 kbps. Files the device already plays (MP3, AAC, WAV) are
copied through untouched, and transcoded files keep their full tags - title,
artist, album, track and disc numbers, genre, and embedded cover art.

| Model | Released | Sync tier | Transcode |
|---|---|---|---|
| iPod 1G | 2001 | Direct iTunesDB | `mp4a` |
| iPod 2G | 2002 | Direct iTunesDB | `mp4a` |
| iPod 3G | 2003 | Direct iTunesDB | `alac` |
| iPod 4G | 2004 | Direct iTunesDB | `alac` |
| iPod Photo | 2004 | Direct iTunesDB | `alac` |
| iPod Video 5G | 2005 | Direct iTunesDB | `alac` |
| iPod Video 5.5G | 2006 | Direct iTunesDB | `alac` |
| iPod Shuffle 1G | 2005 | iTunesSD | `mp4a` |
| iPod Shuffle 2G | 2006 | iTunesSD | `mp4a` |
| iPod Shuffle 3G | 2009 | iTunesSD (bdhs) | `alac` |
| iPod Shuffle 4G | 2010 | iTunesSD (bdhs) | `alac` |
| iPod Mini 1G | 2004 | Direct iTunesDB | `alac` |
| iPod Mini 2G | 2005 | Direct iTunesDB | `alac` |
| iPod Classic 6G | 2007 | hash58 | `alac` |
| iPod Classic 6.5G | 2008 | hash58 | `alac` |
| iPod Classic 7G | 2009 | hash58 | `alac` |
| iPod Nano 1G | 2005 | Direct iTunesDB | `alac` |
| iPod Nano 2G | 2006 | Direct iTunesDB | `alac` |
| iPod Nano 3G | 2007 | hash58 | `alac` |
| iPod Nano 4G | 2008 | hash58 | `alac` |
| iPod Nano 5G | 2009 | hash72 + SQLite | `alac` |

!!! note "Why some models get AAC instead of lossless"
    Four models can't decode Apple Lossless - their firmware silently skips
    ALAC files. The FireWire-era iPod 1G/2G predate ALAC (Apple only shipped
    its decoder to dock-connector models in 2004), and the screenless Shuffles
    didn't get it until 2009. OrgZ transcodes to AAC 256 kbps for those so
    everything on the device actually plays.

The **iPod Nano 6G/7G** and **iPod Touch / iPhone** are detected but not
writable: their databases require a signing scheme with no open-source
implementation.

## Rockbox players

Any player running [Rockbox](https://www.rockbox.org/) - iPods included - needs
no database writing and no transcoding at all: Rockbox plays FLAC and friends
natively, so OrgZ copies your files as-is and the player indexes them itself.
This also applies to generic USB players that mount as a removable drive.

## USB CD-ROM drives

| Drive | Connection | External power | Tested |
|---|---|---|---|
| Pioneer BD-RW BDR-XS07U | USB 3.2 Gen 1, USB-C port on the drive | Not required - USB bus-powered | ✅ CD rip + DAO audio burn |

Have hardware on this list that isn't validated yet? We gladly accept loans or
donations for testing - [open an issue](https://github.com/FoxCouncil/OrgZ/issues).
