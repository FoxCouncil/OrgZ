// Copyright (c) 2025 Fox Diller

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace OrgZ.Services;

internal static class ShortcutInstaller
{
    private const string APP_ID = "com.foxcouncil.orgz";
    private const string APP_NAME = "OrgZ";

    internal static void EnsureShortcut()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var lnkPath = Path.Combine(programs, "OrgZ.lnk");

            var link = (IShellLinkW)new ShellLink();

            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetDescription(APP_NAME);
            link.SetIconLocation(exePath, 0);

            ((IPersistFile)link).Save(lnkPath, true);

            var store = (IPropertyStore)link;

            store.SetValue(PKEY_AppUserModel_ID, PropVariant.FromString(APP_ID));
            store.SetValue(PKEY_AppUserModel_RelaunchDisplayNameResource, PropVariant.FromString(APP_NAME));
            store.SetValue(PKEY_AppUserModel_RelaunchIconResource, PropVariant.FromString($"{exePath},0"));
            store.SetValue(PKEY_AppUserModel_RelaunchCommand, PropVariant.FromString($"\"{exePath}\""));
            store.Commit();

            ((IPersistFile)link).Save(lnkPath, true);
        }
        catch { }
    }

    #region COM

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        uint GetCount();
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PropVariant pv);
        void SetValue(ref PROPERTYKEY key, PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    private static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);

    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);

    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 4);

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;

        public static PropVariant FromString(string s)
        {
            return new PropVariant
            {
                vt = (ushort)VarEnum.VT_LPWSTR,
                pwszVal = Marshal.StringToCoTaskMemUni(s)
            };
        }
    }

    #endregion
}
