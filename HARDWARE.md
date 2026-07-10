# Hardware

OrgZ is developed and validated against real devices - every iPod generation has its own quirks, and only metal proves a write path. We gladly accept hardware donations (or loans) for testing; if you have something on this list that isn't checked off yet, open an issue.

## iPod

Color variants collapsed into one row. Win / Mac = OrgZ tested against the device on that OS. iTunes = co-habitation with iTunes verified. Rockbox = tested running Rockbox firmware.

**Identity decode (serial → model/colour/capacity) is verified against libgpod's tables for every row below** - so the model we'd *show* for any of these is a sound best guess. The **Notes** column flags what hardware would upgrade that guess to confirmed: specifically, reading the serial off that generation's own firmware on macOS/Linux (Windows already gets it from WMI). A blank Notes cell means fully confirmed.

| Model | Released | Sync tier | Win | Mac | iTunes | Rockbox | Notes |
|---|---|---|---|---|---|---|---|
| iPod 1G | 2001 | None (direct iTunesDB) | | | | | |
| iPod 2G | 2002 | None (direct iTunesDB) | | | | | |
| iPod 3G | 2003 | None (direct iTunesDB) | | | | | |
| iPod 4G | 2004 | None (direct iTunesDB) | | | | | |
| iPod Photo | 2004 | None (direct iTunesDB) | | | | | |
| iPod Video 5G | 2005 | None (direct iTunesDB) | | | | | |
| iPod Video 5.5G | 2006 | None (direct iTunesDB) | ✅ | ✅ | | | ✅ |
| iPod Shuffle 1G | 2005 | iTunesSD | | | | | NEEDED |
| iPod Shuffle 2G | 2006 | iTunesSD | | | | | |
| iPod Shuffle 3G | 2009 | iTunesSD | | | | | |
| iPod Shuffle 4G | 2010 | iTunesSD | | | | | |
| iPod Mini 1G | 2004 | None (direct iTunesDB) | | | | | Needed |
| iPod Mini 2G | 2005 | None (direct iTunesDB) | | | | | |
| iPod Classic 6G | 2007 | hash58 | | | | | |
| iPod Classic 6.5G | 2008 | hash58 | | | | | |
| iPod Classic 7G | 2009 | hash58 | | | | | |
| iPod Nano 1G | 2005 | None (direct iTunesDB) | | | | | NEEDED to dump |
| iPod Nano 2G | 2006 | None (direct iTunesDB) | | | | | |
| iPod Nano 3G | 2007 | hash58 | | | | | |
| iPod Nano 4G | 2008 | hash58 | | | | | |
| iPod Nano 5G | 2009 | hash72 + SQLite | ✅ | | ✅ | | ✅ |

iPod Touch and iPhone are out of scope

## USB CD-ROM

| Drive | Connection | External power | Tested |
|---|---|---|---|
| Pioneer BD-RW BDR-XS07U | USB 3.2 Gen 1, USB-C port on the drive | Not required - USB bus-powered | ✅ CD rip + DAO audio burn |
