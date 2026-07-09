# libgpod write-verification oracle

`gpod_dump.c` is the **independent oracle** for slice C: it links libgpod's own `itdb_parse` - the
canonical reference implementation - and dumps every load-bearing track and playlist field as JSON.
OrgZ's writer emits a database; this reads it back; the test asserts libgpod saw exactly what OrgZ
intended. It deliberately never uses OrgZ's own reader - writing and reading with the same code is the
circularity that hid real bugs (an MHBD dataset-count of 0 made the whole DB unreadable; MHIPs without
a type-100 position MHOD produced an *empty on-device song list* even though the tracks were on disk).

## What the tests do

- `ITunesDbWriterOracleTests.Emit_reproduces_the_libgpod_blessed_bytes` runs everywhere (including
  Windows CI): OrgZ re-emits the sample library and must reproduce, byte-for-byte,
  `Fixtures/itunesdb-write/orgz-emitted.iTunesDB` - the exact database libgpod blessed.
- `ITunesDbWriterOracleTests.Libgpod_reads_back_every_field` runs the live oracle when
  `ORGZ_GPOD_DUMP` points at a built `gpod_dump`, and diffs its JSON against
  `Fixtures/itunesdb-write/libgpod-golden.jsonl`.

## Run the live oracle (Linux / Docker)

From the repo root, build the oracle and run the whole test project against it:

```sh
docker run --rm -v "$PWD:/src:ro" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc '
  apt-get update -qq && apt-get install -y -qq libgpod-dev gcc pkg-config >/dev/null
  cp -r /src/* /work/
  gcc OrgZ.Tests/oracle/gpod_dump.c -o /tmp/gpod_dump $(pkg-config --cflags --libs libgpod-1.0)
  ORGZ_GPOD_DUMP=/tmp/gpod_dump dotnet test OrgZ.Tests/OrgZ.Tests.csproj \
    --filter FullyQualifiedName~ITunesDbWriterOracle --nologo
'
```

## Regenerate the golden after an intentional writer change

Both fixtures are re-blessed together so a writer change can never be silently accepted:

1. Emit the sample DB (run `Emit_reproduces_the_libgpod_blessed_bytes`; it writes
   `bin/.../oracle-out/iPod_Control/iTunes/iTunesDB`).
2. Copy that file over `Fixtures/itunesdb-write/orgz-emitted.iTunesDB`.
3. Run `gpod_dump` on the `oracle-out` mountpoint and save its stdout to
   `Fixtures/itunesdb-write/libgpod-golden.jsonl`.
4. Re-run the tests; both must pass.

## hash58 oracle (`hash58_independent.py`)

`hash58_independent.py` is a second, independent implementation of the hash58 checksum used to verify
`ITunesDbHash58` (`ITunesDbHash58OracleTests`). It generates the AES S-box from GF(2^8) first
principles and uses Python's stdlib HMAC-SHA1, so it shares nothing with OrgZ's port but the documented
`Fixed[]` constant and the algorithm - agreement rules out a porting bug the self-consistency tests
can't see. Reproduce the reference hash directly:

```sh
python OrgZ.Tests/oracle/hash58_independent.py \
  OrgZ.Tests/Fixtures/itunesdb-write/orgz-emitted.iTunesDB 000A27001597690A
# -> independent_hash58=a986963f9d5808bad66a167a48460cc723878ccb
```

The canonical libgpod-binary cross-check (`itdb_hash58_write_hash`) is the further confirmation still to
add when the Docker daemon is healthy.

## artwork oracle (`artwork_dump.c`)

`artwork_dump.c` reads an iPod mountpoint with libgpod (`itdb_parse`, which also parses the ArtworkDB)
and, per track, reports the artwork libgpod links to it - the linkage dbid, and the thumbnail decoded
via `itdb_artwork_get_pixbuf`: its native dimensions and a sampled pixel. It verifies OrgZ's
`ArtworkDbWriter` + RGB565 `.ithmb` (`ITunesDbArtworkOracleTests`).

libgpod only parses the ArtworkDB for a device it recognises as supporting cover art, so the mountpoint
needs `iPod_Control/Device/SysInfo` with a model. `get_ipod_info_from_model_number` drops **one** leading
letter, so `ModelNumStr: MA446` → `A446` → iPod Video 5.5G (whereas `xMA446` → `MA446`, which does not
match). Build + run (needs gdk-pixbuf as well as libgpod):

```sh
docker run --rm -v "$PWD:/src:ro" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc '
  apt-get update -qq && apt-get install -y -qq libgpod-dev libgdk-pixbuf-2.0-dev gcc pkg-config >/dev/null
  cp -r /src/* /work/
  gcc OrgZ.Tests/oracle/artwork_dump.c -o /tmp/artwork_dump $(pkg-config --cflags --libs libgpod-1.0 gdk-pixbuf-2.0)
  ORGZ_ARTWORK_DUMP=/tmp/artwork_dump dotnet test OrgZ.Tests/OrgZ.Tests.csproj \
    --filter FullyQualifiedName~ITunesDbArtworkOracle --nologo
'
```

(When Docker is unavailable, the same works under WSL Ubuntu with `libgpod-dev` + `libgdk-pixbuf-2.0-dev`
installed as root - that is how it was verified.)
