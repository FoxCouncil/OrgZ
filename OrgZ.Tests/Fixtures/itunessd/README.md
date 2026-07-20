# Real iTunesSD fixture

`shuffle2g-itunes.itunessd` is a byte-for-byte copy of the `iTunesSD` that Mac iTunes wrote to a real
iPod Shuffle 2G (A1204, 1GB) - 18-byte header + 21 × 558-byte entries, captured 2026-07-19 off the
device exactly as its last iTunes sync (2023-08-02) left it. It contains only iTunes-obfuscated device
paths (`/iPod_Control/Music/F02/RLED.m4a` style) - no metadata, no personal data.

It exists to keep `ShuffleSdWriter` honest against **real Apple output**, not just the WikiPodLinux /
libgpod spec the writer was authored from. Two things the real bytes settled that the spec alone didn't:

- Header field 2 is `0x010800` (libgpod writes the older `0x010600`); we match Apple.
- iTunes writes **volume `0`** on every track, not the spec's "100 = neutral" - `0` means "no
  adjustment" on this firmware (this device played for years with `0` on every entry).

Ground truth (from the capture):

| Field | Value |
|---|---|
| Track count | 21 |
| Header | `00 00 15 01 08 00 00 00 12` + 9 zero bytes |
| Entry 0 path | `/iPod_Control/Music/F02/RLED.m4a` |
| All entries | file type 2 (AAC), volume 0, start/stop 0, shuffle flag 1, bookmark flag 0 |

The round-trip test locks in that `Read` → `Write` reproduces the file **byte-identically**, so any
drift in either direction from Apple's real output fails loudly.
