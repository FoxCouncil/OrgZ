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

    **Architecture:** Apple Silicon and Intel · **Requires:** macOS 11 (Big Sur) or newer

    Two builds, one per architecture - pick the one matching your Mac. If you are
    unsure, open the Apple menu → **About This Mac**: an Apple M-series chip means Apple
    Silicon, anything Intel means the Intel build.

    | Download | Use it when |
    |----------|-------------|
    | `OrgZ-osx-Setup.pkg` | Apple Silicon (M1 or newer). Normal install into `/Applications`. |
    | `OrgZ-osx-Portable.zip` | Apple Silicon, no-install `OrgZ.app` you can run from anywhere. |
    | `OrgZ-osx-x64-Setup.pkg` | Intel Mac. Normal install into `/Applications`. |
    | `OrgZ-osx-x64-Portable.zip` | Intel Mac, no-install `OrgZ.app`. |

    `libvlc` is bundled inside the app, so playback works with no extra setup.

    1. Download the `.pkg` for your architecture.
    2. Double-click it and follow Installer. The package is signed with a Developer
       ID certificate and notarized by Apple, so Gatekeeper opens it without a
       warning and without any quarantine workaround.
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
