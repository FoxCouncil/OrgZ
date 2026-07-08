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
