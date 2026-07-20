# Hardware

OrgZ is developed and validated against real devices - every iPod generation has its own quirks, and only metal proves a write path. We gladly accept hardware donations (or loans) for testing; if you have something on this list that isn't checked off yet, open an issue.

## iPod

Color variants collapsed into one row. Win / Mac = OrgZ tested against the device on that OS. iTunes = co-habitation with iTunes verified. Rockbox = tested running Rockbox firmware.

**Identity decode (serial → model/colour/capacity) is verified against libgpod's tables for every row below** - so the model we'd *show* for any of these is a sound best guess. The **Notes** column flags what hardware would upgrade that guess to confirmed: specifically, reading the serial off that generation's own firmware on macOS/Linux (Windows already gets it from WMI). A blank Notes cell means fully confirmed.

**Transcode** is the codec fourCC OrgZ targets when a source file needs converting for that model (FLAC/OGG/etc - natively playable files always copy through untouched): `alac` = Apple Lossless, `mp4a` = AAC 256 kbps. Four models can't decode ALAC and get AAC instead: the FireWire-era iPod 1G/2G (Apple only shipped ALAC decode to dock-connector models, mid-2004 firmware) and the Shuffle 1G/2G (ALAC arrived with the Shuffle 3G) - hardware-confirmed on a real Shuffle 2G, where a valid ALAC file is silently skipped.

| Model | Released | Sync tier | Transcode | Win | Mac | iTunes | Rockbox | Notes |
|---|---|---|---|---|---|---|---|---|
| iPod 1G | 2001 | None (direct iTunesDB) | mp4a | | | | | |
| iPod 2G | 2002 | None (direct iTunesDB) | mp4a | | | | | |
| iPod 3G | 2003 | None (direct iTunesDB) | alac | | | | | |
| iPod 4G | 2004 | None (direct iTunesDB) | alac | | | | | |
| iPod Photo | 2004 | None (direct iTunesDB) | alac | | | | | |
| iPod Video 5G | 2005 | None (direct iTunesDB) | alac | | | | | |
| iPod Video 5.5G | 2006 | None (direct iTunesDB) | alac | ✅ | ✅ | | | ✅ |
| iPod Shuffle 1G | 2005 | iTunesSD | mp4a | | | | | NEEDED |
| iPod Shuffle 2G | 2006 | iTunesSD | mp4a | ✅ | | | | |
| iPod Shuffle 3G | 2009 | iTunesSD | alac | | | | | |
| iPod Shuffle 4G | 2010 | iTunesSD | alac | | | | | |
| iPod Mini 1G | 2004 | None (direct iTunesDB) | alac | | | | | Needed |
| iPod Mini 2G | 2005 | None (direct iTunesDB) | alac | | | | | |
| iPod Classic 6G | 2007 | hash58 | alac | | | | | |
| iPod Classic 6.5G | 2008 | hash58 | alac | | | | | |
| iPod Classic 7G | 2009 | hash58 | alac | | | | | |
| iPod Nano 1G | 2005 | None (direct iTunesDB) | alac | | | | | NEEDED to dump |
| iPod Nano 2G | 2006 | None (direct iTunesDB) | alac | | | | | |
| iPod Nano 3G | 2007 | hash58 | alac | ✅ | | | | |
| iPod Nano 4G | 2008 | hash58 | alac | | | | | |
| iPod Nano 5G | 2009 | hash72 + SQLite | alac | ✅ | | ✅ | | ✅ |

iPod Touch and iPhone are out of scope

## USB CD-ROM

| Drive | Connection | External power | Tested |
|---|---|---|---|
| Pioneer BD-RW BDR-XS07U | USB 3.2 Gen 1, USB-C port on the drive | Not required - USB bus-powered | ✅ CD rip + DAO audio burn |
