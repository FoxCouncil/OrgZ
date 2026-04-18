// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Cross-platform "safely remove" for mounted removable devices. On Windows this
/// delegates to the Shell.Application COM object's "Eject" verb, which is exactly
/// what Explorer invokes when you right-click a USB drive → Eject — it flushes the
/// volume, locks it, dismounts the filesystem, and powers down the USB port.
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
        // On macOS/Linux the mount path is under /Volumes or /media; umount(8) is the
        // canonical path but that needs root. Leave a no-op stub for now.
        error = "eject not implemented on this platform";
        return false;
#endif
    }
}
