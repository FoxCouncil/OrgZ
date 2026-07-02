# Synthetic Nano 5G fixture

A committable stand-in for a real iPod Nano 5G's `iTunes Library.itlp` SQLite stack, so the whole
Hash72 tier (writer, cbk re-sign, CDB regeneration, `Nano5gIPod` interface CRUD) tests honestly on
any machine - no hardware, no private device data.

## Contents

| File | What it carries |
|---|---|
| `Library.itdb` | Full device schema; rows only for `db_info` (scrubbed), `version_info`, `genre_map`, `category_map`, `location_kind_map`, and the primary container (renamed `OrgZ`). Zero items/albums/artists. |
| `Locations.itdb` | Full schema; only the `base_location` row (`iPod_Control/Music`). Zero locations. |
| `Dynamic.itdb` | Full schema; only the primary container's `container_ui` row. Zero item stats. |
| `Locations.itdb.cbk` | Built by `ITunesLocationsCbk.Build` with a fabricated, deterministic seed (iv `0x51..`, rnd `0xC3..`) - NOT any device's. Valid because hash72 only needs a consistent pair; the writer recovers whatever seed the cbk carries and re-signs. |

Scrubbed from the source capture: `db_info.pid` (synthetic constant), `genius_cuid`, the `bib`/`rib`
blobs, and the container name (device volume name). A post-scrub check asserts no blob survives
anywhere and every content table is empty.

## Regenerating (schema drift, new firmware capture)

1. Have a real capture at `%LOCALAPPDATA%\OrgZ\nano5g-fixture` (`Library.itdb`, `Locations.itdb`,
   `Locations.itdb.cbk`, `Dynamic.itdb`).
2. Run the scrub script (schema copy + structural rows + identity scrub + verification) -
   see `Nano5gLibraryWriterTests.FixtureDirs` for what consumes this. The script lives in the
   commit that introduced this fixture.
3. Rebuild the cbk against the new `Locations.itdb` with `ITunesLocationsCbk.Build` and any
   fixed 16-byte iv / 12-byte rnd, and verify `TryExtractSeed` round-trips.
