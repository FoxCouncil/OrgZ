# Hardware

OrgZ is developed and validated against real devices - every iPod generation has its own quirks, and only metal proves a write path. We gladly accept hardware donations (or loans) for testing; if you have something on this list that isn't checked off yet, open an issue.

## iPod

Color variants collapsed into one row. Win / Mac = OrgZ tested against the device on that OS. iTunes = co-habitation with iTunes verified. Rockbox = tested running Rockbox firmware.

**Identity decode (serial → model/colour/capacity) is verified against libgpod's tables for every row below** - so the model we'd *show* for any of these is a sound best guess. The **Notes** column flags what hardware would upgrade that guess to confirmed: specifically, reading the serial off that generation's own firmware on macOS/Linux (Windows already gets it from WMI). A blank Notes cell means fully confirmed.

| Model | Released | Sync tier | Win | Mac | iTunes | Rockbox | Notes |
|---|---|---|---|---|---|---|---|
| iPod 1G | 2001 | None (direct iTunesDB) | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit to dump |
| iPod 2G | 2002 | None (direct iTunesDB) | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod 3G | 2003 | None (direct iTunesDB) | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod 4G | 2004 | None (direct iTunesDB) | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod Photo | 2004 | None (direct iTunesDB) | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod Video 5G | 2005 | None (direct iTunesDB) | | | | | Assumes 5.5G HDD SysInfo layout - a dump confirms |
| iPod Video 5.5G | 2006 | None (direct iTunesDB) | ✅ | ✅ | | | ✅ identity confirmed on real hardware (serial→model off flash) |
| iPod Shuffle 1G | 2005 | iTunesSD | | | | | Both WMI + firmware serial paths unproven - need any unit |
| iPod Shuffle 2G | 2006 | iTunesSD | | | | | Both WMI + firmware serial paths unproven - need any unit |
| iPod Shuffle 3G | 2009 | iTunesSD | | | | | Both WMI + firmware serial paths unproven - need any unit |
| iPod Shuffle 4G | 2010 | iTunesSD | | | | | Both WMI + firmware serial paths unproven - need any unit |
| iPod Mini 1G | 2004 | None (direct iTunesDB) | | | | | Microdrive HDD firmware read unproven - need any unit |
| iPod Mini 2G | 2005 | None (direct iTunesDB) | | | | | Microdrive HDD firmware read unproven - need any unit |
| iPod Classic 6G | 2007 | hash58 | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod Classic 6.5G | 2008 | hash58 | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod Classic 7G | 2009 | hash58 | | | | | HDD firmware-serial read unproven on Mac/Linux - need any unit |
| iPod Nano 1G | 2005 | None (direct iTunesDB) | | | | | Flash firmware serial layout unconfirmed - need any unit to dump |
| iPod Nano 2G | 2006 | None (direct iTunesDB) | | | | | Flash firmware serial layout unconfirmed - need any unit |
| iPod Nano 3G | 2007 | hash58 | | | | | NOR `SCfg` parser is code-only - need a Nano 3G NOR dump |
| iPod Nano 4G | 2008 | hash58 | | | | | NOR `SCfg` parser is code-only - need a Nano 4G NOR dump |
| iPod Nano 5G | 2009 | hash72 + SQLite | ✅ | | ✅ | | ✅ write-sync proven on real hardware (identity out of the 1G-Nano 4G scope) |

iPod Touch and iPhone are out of scope - iOS devices carry no on-disk iTunesDB to write.

## USB CD-ROM

| Drive | Connection | External power | Tested |
|---|---|---|---|
| Pioneer BD-RW BDR-XS07U | USB 3.2 Gen 1, USB-C port on the drive | Not required - USB bus-powered | ✅ CD rip + DAO audio burn |
