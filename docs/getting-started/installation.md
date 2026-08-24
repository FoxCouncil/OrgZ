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
    | `OrgZ-win.msi` | Normal install. Installs for all users into Program Files, adds Start Menu / desktop entries, and registers the background device helper. |
    | `OrgZ-win-Portable.zip` | No-install copy you can run from a folder or USB stick. |

    1. Download `OrgZ-win.msi` and run it. Windows asks for consent once, during
       the install.
    2. Launch OrgZ. It checks for updates quietly at startup and tells you through
       the **Help** menu when one is waiting.

    !!! note "Disc and iPod access, without a prompt every time"
        Reading raw CD audio and iPod firmware uses `IOCTL_SCSI_PASS_THROUGH`, which
        requires administrator rights. The MSI registers a small background service at
        install time, so those operations run silently afterwards - the one consent
        prompt during installation replaces a UAC prompt per rip and per device.

        A **portable** copy has no installer and therefore no service, so it falls
        back to prompting each time. See [Ripping CDs](../features/ripping-cds.md).

=== "macOS"

    **Architecture:** Apple Silicon (M1 or newer) · **Requires:** a recent macOS

    !!! warning "Apple Silicon only"
        There is currently no Intel (x86-64) build. OrgZ runs on M1/M2/M3-class
        Macs. `libvlc` is bundled inside the app, so playback works with no extra setup.

    | Download | Use it when |
    |----------|-------------|
    | `OrgZ-osx-Setup.pkg` | Normal install into `/Applications`. |
    | `OrgZ-osx-Portable.zip` | No-install `OrgZ.app` you can run from anywhere. |

    1. Download `OrgZ-osx-Setup.pkg`.
    2. The build is **not signed or notarized yet**, so Gatekeeper blocks the
       installer the first time you open it. On **macOS 15 (Sequoia) and later**,
       double-click the `.pkg`, let macOS refuse it, then open **System Settings →
       Privacy & Security**, scroll to the message naming the blocked package, and
       click **Open Anyway**. Confirm, and Installer runs.

        Apple removed the Control-click → **Open** override in Sequoia; on older
        macOS releases that shortcut still works.

        From a terminal, clearing the quarantine flag on the download does the same
        thing:

        ```bash
        xattr -dr com.apple.quarantine ~/Downloads/OrgZ-osx-Setup.pkg
        ```

    3. Launch OrgZ from `/Applications`.

    !!! note "Encoders are already in the bundle"
        `ffmpeg`, `flac`, and `lame` ship inside the `.app` - there is nothing to
        install with Homebrew for ripping, burning, or iPod transcoding. See
        [Ripping CDs](../features/ripping-cds.md).

=== "Linux"

    **Architecture:** x64 · **Format:** AppImage

    OrgZ is distributed as a single self-contained `OrgZ.AppImage`. `libvlc` for
    playback and static `ffmpeg`, `flac`, and `lame` binaries for ripping and
    transcoding all travel inside it, so there is nothing to install first. Audio
    plays through PulseAudio / PipeWire.

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

    3. If it exits without opening a window, VLC is missing (see above). OrgZ
       says so on stderr and - where `zenity` or `kdialog` is installed - in a
       dialog. Either way the reason is in the log:

        ```bash
        tail ~/.local/state/OrgZ/logs/orgz-*.log
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
per-user data directory. Your **music files are never moved**: OrgZ only
reads from the library folder you point it at, writes ripped tracks and
downloaded audiobooks there (see [Ripping CDs](../features/ripping-cds.md)),
and deletes from it only when you choose **Remove from Library**, which asks
first - see [Music Library](../features/music-library.md).

## Updating

OrgZ checks for a new version quietly at startup and never acts on its own. When
one is waiting, the **Help** menu changes to *There are updates...* - choosing it
downloads the update, asks for consent where the platform requires it, and restarts
into the new version. Nothing is downloaded or installed until you ask for it.

The check runs on Windows, macOS, and Linux alike. It is skipped for a copy with
nothing to update - a portable `.zip` / `.app`, or a build run from source - and
the Help menu then simply never announces one; re-download the latest artifact
for those.
