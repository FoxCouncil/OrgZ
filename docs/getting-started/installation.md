# Installation

OrgZ ships for Windows, macOS, and Linux. Grab the latest build from
[GitHub Releases](https://github.com/FoxCouncil/OrgZ/releases) and follow the
tab for your platform.

=== "Windows"

    **Architecture:** x64 · **Requires:** Windows 10 or later

    OrgZ is distributed as a self-updating application via
    [Velopack](https://velopack.io/). The .NET runtime is bundled - there is
    nothing else to install.

    | Download | Use it when |
    |----------|-------------|
    | `OrgZ-win-Setup.exe` | Normal install. Adds Start Menu / desktop entries and auto-updates on launch. |
    | `OrgZ-win-Portable.zip` | No-install copy you can run from a folder or USB stick. |

    1. Download `OrgZ-win-Setup.exe` and run it.
    2. Launch OrgZ. It checks for updates on startup and applies them in the background.

    !!! note "Ripping CDs needs elevation"
        Reading raw CD audio uses `IOCTL_SCSI_PASS_THROUGH`, which requires
        administrator rights. OrgZ relaunches an elevated helper **per rip**, so
        you'll see a UAC prompt when you start ripping. Everyday playback and
        library management do not need elevation. See [Ripping CDs](../features/ripping-cds.md).

=== "macOS"

    **Architecture:** Apple Silicon (M1 or newer) · **Requires:** a recent macOS

    !!! warning "Apple Silicon only"
        There is currently no Intel (x86-64) build. OrgZ runs on M1/M2/M3-class
        Macs. `libvlc` is bundled inside the app, so playback works out of the box.

    | Download | Use it when |
    |----------|-------------|
    | `OrgZ-osx-Setup.pkg` | Normal install into `/Applications`. |
    | `OrgZ-osx-Portable.zip` | No-install `OrgZ.app` you can run from anywhere. |

    1. Download `OrgZ-osx-Setup.pkg` and run it.
    2. The build is **not notarized**, so Gatekeeper will complain the first time.
       Clear the quarantine flag, then launch:

        ```bash
        xattr -dr com.apple.quarantine /Applications/OrgZ.app
        open /Applications/OrgZ.app
        ```

        Or: right-click the app → **Open** → **Open** in the dialog, which adds a
        permanent exception.

    !!! tip "For CD ripping"
        Install the encoders if you want FLAC/MP3 output:

        ```bash
        brew install flac lame
        ```

=== "Linux"

    **Architecture:** x64 · **Format:** AppImage

    OrgZ is distributed as a single self-contained `OrgZ.AppImage`. Static
    `flac` and `lame` binaries are bundled for CD ripping, and audio plays
    through PulseAudio / PipeWire.

    1. Download `OrgZ.AppImage`, make it executable, and run it:

        ```bash
        chmod +x OrgZ.AppImage
        ./OrgZ.AppImage
        ```

    2. If it fails to start with a FUSE error, install the FUSE 2 runtime your
       distro ships AppImages against:

        ```bash
        sudo apt install libfuse2      # Debian / Ubuntu
        ```

    !!! note "CD device access"
        Ripping reads the optical drive directly (`/dev/sr0`, ...). Your user needs
        read access to that device - on most distros that means membership in the
        `cdrom` group:

        ```bash
        sudo usermod -aG cdrom "$USER"   # log out / back in afterward
        ```

        The bundled `flac`/`lame` are used automatically; a system install on
        `PATH` (`sudo apt install flac lame`) takes precedence if present.

## Where OrgZ stores your data

OrgZ keeps your library index, settings, device caches, and logs in a
per-user data directory. Your **music files are never moved** - OrgZ only
reads from the library folder you point it at (and writes ripped tracks there;
see [Ripping CDs](../features/ripping-cds.md)).

## Updating

On Windows, OrgZ updates itself on launch. On macOS and Linux, re-download the
latest release artifact and reinstall over the top when a new version ships.
