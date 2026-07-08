// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Cross-platform "safely remove" for mounted removable devices. On Windows this
/// delegates to the Shell.Application COM object's "Eject" verb, which is exactly
/// what Explorer invokes when you right-click a USB drive → Eject - it flushes the
/// volume, locks it, dismounts the filesystem, and powers down the USB port.
/// On macOS it runs <c>diskutil eject</c>, which unmounts every volume on the disk
/// and sends the SCSI eject - a classic iPod then withdraws its medium and shows
/// "OK to disconnect", and won't re-present storage until physically replugged.
/// </summary>
public static class DeviceEjector
{
    /// <summary>
    /// Attempts to eject the drive at <paramref name="mountPath"/>. Returns true
    /// if the shell reported success. A successful eject will cause the WMI volume
    /// watcher to fire a removal event, which the detection service handles.
    /// </summary>
    public static bool Eject(string mountPath, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(mountPath))
        {
            error = "mount path is empty";
            return false;
        }

        // Our own volume hold would make the unmount fail with "busy" - release it first.
        DeviceVolumeHold.Release(mountPath);

#if WINDOWS
        try
        {
            // ssfDRIVES (17) is the "My Computer" / "This PC" shell folder. ParseName
            // resolves a drive letter within it to a FolderItem we can invoke verbs on.
            var letter = mountPath.TrimEnd('\\', '/');
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                error = "Shell.Application COM component not available";
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                var drives = shell.Namespace(17);
                var drive = drives?.ParseName(letter);
                if (drive == null)
                {
                    error = $"shell couldn't find drive '{letter}'";
                    return false;
                }

                drive.InvokeVerb("Eject");
                return true;
            }
            finally
            {
                // Release the COM wrapper so we don't leave refs dangling
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
#else
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/sbin/diskutil",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("eject");
                psi.ArgumentList.Add(mountPath.TrimEnd('/'));

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null)
                {
                    error = "failed to start diskutil";
                    return false;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30_000))
                {
                    process.Kill(entireProcessTree: true);
                    error = "diskutil eject timed out";
                    return false;
                }

                if (process.ExitCode == 0)
                {
                    return true;
                }

                error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Linux: udisksctl would be the non-root path - not wired up yet.
        error = "eject not implemented on this platform";
        return false;
#endif
    }
}
