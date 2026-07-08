# Real iTunesDB fixture

`bripod-3tracks.itunesdb` is a minimal but **structurally real** iTunesDB: the first three `mhit`
track records (with their `mhod` string children - title, album, artist, path, etc.) lifted verbatim
from an iTunes-written database on a real iPod Video 5.5G ("BriPod"), wrapped in a freshly-built
`mhbd → mhsd(type 1) → mhlt` envelope. The track bytes are untouched iTunes output - the offsets,
field widths and endianness are exactly what the firmware and libgpod see.

It exists to keep `ITunesDbReader` honest against **real** bytes, not a fixture the parser co-authored.
The load-bearing regression it locks in: total-discs lives as a `u16` at `0x60`, so a track that is
"disc 1 of 1" must read `TotalDiscs == 1`. The pre-slice-B reader used `ReadInt32(0x5E)`, which spans
`0x5E-0x61` and swept up the low byte of the `0x60` field, returning `65536`. Any reader that
reintroduces that bug fails `ITunesDbRealFixtureTests`.

Ground truth (from the source device):

| # | Title            | Disc | Total discs | Rating | Duration (ms) | File size |
|---|------------------|------|-------------|--------|---------------|-----------|
| 0 | Polaris          | 1    | 1           | 0      | 288235        | 10136119  |
| 1 | Salt Water Sound | 1    | 1           | 0      | 330893        | 11200042  |
| 2 | Distractions     | 1    | 1           | 0      | 316421        | 10099969  |
