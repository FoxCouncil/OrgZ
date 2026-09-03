# Hardware limits

Every player OrgZ writes to has edges: a filesystem ceiling, a folder that gets too full, a
database the firmware can no longer hold in memory. OrgZ knows these edges, keeps its writes
inside them, and checks the device against them after every sync.

## What OrgZ checks

After a sync finishes, OrgZ reads the device back and checks:

- every music track is marked as music (a 6G+ iPod hides untyped tracks from its menus)
- every track that claims cover art actually has it stored
- cover art is stored once per picture, not once per track
- every playlist entry points at a track that exists
- no artwork file is at the filesystem's size ceiling
- no music folder is near the filesystem's entry limit
- the database is within what the model can load into memory
- there is working room left on the volume

The status line reports the worst finding; the full list is in the log. The same checks run
before a sync starts, so a device that already has a problem is reported before hours of
copying.

## The limits

| Limit | Value | Applies to | Where the number comes from |
|-------|-------|------------|-----------------------------|
| Largest file | 4 GiB − 1 byte | every player (FAT32) | FAT32 specification |
| Entries per folder | 65,534 | every player (FAT32) | FAT32 specification |
| Largest volume | 2 TiB | every player (MBR, 512-byte sectors) | FAT32 / MBR specification |
| Music folders | 50 (`F00`–`F49`) | stock iPods | how iTunes lays a device out |
| Artwork file roll-over | just under 4 GiB | stock iPods | FAT32; failure seen on a Classic 7G |
| Database in memory | ~50 MB on 64 MB Classics, ~25 MB on 32 MB Classics | Classic 6G / 6.5G / 7G | community reports from flash-modded Classics |
| Track count | ~50,000 on 64 MB Classics, ~20–25,000 on 32 MB | Classic 6G / 6.5G / 7G | community reports |
| Browser entries per folder | 400 (adjustable to 10,000) | Rockbox | Rockbox manual, *System → Limits* |

Which Classic has which RAM: the 160 GB models (6G and 7G) have 64 MB; the 80 GB and 120 GB
models have 32 MB. A flash-modded Classic keeps whatever RAM it shipped with, however big its
new storage is.

## How the writes stay inside them

- **Tracks** go into one of fifty folders, chosen at random the way iTunes does it, so no
  folder grows huge and slow.
- **Cover art** is stored once per picture, and a format's thumbnail file rolls over to the
  next numbered file (`F1060_2.ithmb`) before it can reach the size ceiling.
- **Large libraries** are checked against the model's database budget so a sync can warn
  before the firmware can't load the result.

## Adding a new kind of player

Limits live in one catalog, contributed by one provider per platform (FAT32, stock iPod,
Rockbox). A new player is a new provider that lists its own numbers; the checks pick them up
by name and need no changes. Where two providers speak to the same limit, the stricter number
wins.
