# Device Image Attributions

iPod product illustrations in this directory are sourced from Wikimedia Commons
and distributed under the Creative Commons Attribution-ShareAlike 3.0 Unported
license (CC BY-SA 3.0) and/or the GNU Free Documentation License (GFDL 1.2+).

You are free to redistribute these images under the same terms.

## Files

| Local file              | Source (Wikimedia Commons)                                                                           | License            |
| ----------------------- | ---------------------------------------------------------------------------------------------------- | ------------------ |
| `ipod_classic_6g.png`   | [6G_iPod.svg](https://commons.wikimedia.org/wiki/File:6G_iPod.svg)                                   | CC BY-SA 3.0, GFDL |
| `ipod_classic_6_5g.png` | copy of `ipod_classic_6g.png` (identical form factor)                                                | CC BY-SA 3.0, GFDL |
| `ipod_classic_7g.png`   | copy of `ipod_classic_6g.png` (identical form factor)                                                | CC BY-SA 3.0, GFDL |
| `ipod_video_5g.png`     | copy of `ipod_classic_6g.png` (identical front face)                                                 | CC BY-SA 3.0, GFDL |
| `ipod_video_5_5g.png`   | copy of `ipod_classic_6g.png` (identical front face)                                                 | CC BY-SA 3.0, GFDL |
| `ipod_4g.png`           | copy of `ipod_classic_6g.png` (closest approximation)                                                | CC BY-SA 3.0, GFDL |
| `ipod_photo.png`        | copy of `ipod_classic_6g.png` (closest approximation)                                                | CC BY-SA 3.0, GFDL |
| `ipod_mini_1g.png`      | [Mini_iPod.svg](https://commons.wikimedia.org/wiki/File:Mini_iPod.svg)                               | CC BY-SA 3.0, GFDL |
| `ipod_mini_2g.png`      | copy of `ipod_mini_1g.png` (identical form factor)                                                   | CC BY-SA 3.0, GFDL |
| `ipod_nano_1g.png`      | [1G_Nano_iPod.svg](https://commons.wikimedia.org/wiki/File:1G_Nano_iPod.svg)                         | CC BY-SA 3.0, GFDL |
| `ipod_nano_2g.png`      | [2G_Nano_iPod.svg](https://commons.wikimedia.org/wiki/File:2G_Nano_iPod.svg)                         | CC BY-SA 3.0, GFDL |
| `ipod_nano_7g.png`      | [7th_Generation_iPod_Nano.svg](https://commons.wikimedia.org/wiki/File:7th_Generation_iPod_Nano.svg) | CC BY-SA 3.0, GFDL |
| `ipod_shuffle_4g.png`   | [IPod_Shuffle_4G.svg](https://commons.wikimedia.org/wiki/File:IPod_Shuffle_4G.svg)                   | CC BY-SA 3.0, GFDL |

## Gaps to fill

The following generations from libgpod's table don't yet have a dedicated image — they
currently fall back to the FontAwesome icon:

- `1g`, `2g`, `3g` — original iPod with mechanical/touch scroll wheels
- `nano_3g` — fat video Nano (2007)
- `nano_4g` — tall Nano (2008)
- `nano_5g` — Nano with camera (2009)
- `nano_6g` — square touch Nano (2010)
- `shuffle_1g`, `shuffle_2g`, `shuffle_3g`
- `touch_1g`, `touch_2g`, `touch_3g`, `touch_4g`

Drop a matching `ipod_{slug}.png` into this folder and add its slug to
`KnownGenerationImages` in `Models/ConnectedDevice.cs`.
