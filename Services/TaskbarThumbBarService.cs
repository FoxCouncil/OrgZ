// Copyright (c) 2025 Fox Diller

using System.Runtime.InteropServices;

namespace OrgZ.Services;

#region COM

[ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
internal class TaskbarListClass { }

[ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList (slots 3-7)
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(IntPtr hwnd);
    [PreserveSig] int DeleteTab(IntPtr hwnd);
    [PreserveSig] int ActivateTab(IntPtr hwnd);
    [PreserveSig] int SetActiveAlt(IntPtr hwnd);

    // ITaskbarList2 (slot 8)
    [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ITaskbarList3 (slots 9-20)
    [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    [PreserveSig] int SetProgressState(IntPtr hwnd, int tbpFlags);
    [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
    [PreserveSig] int UnregisterTab(IntPtr hwndTab);
    [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
    [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
    [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
    [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
    [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
    [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
internal struct THUMBBUTTON
{
    public uint dwMask;
    public uint iId;
    public uint iBitmap;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szTip;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    [MarshalAs(UnmanagedType.Bool)]
    public bool fIcon;
    public int xHotspot;
    public int yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

#endregion

#region P/Invoke

internal delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

internal static class ThumbBarNativeMethods
{
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const int SM_CXSMICON = 49;
}

#endregion

internal sealed class TaskbarThumbBarService : IDisposable
{
    private const uint WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;

    private const uint THB_ICON = 0x2;
    private const uint THB_TOOLTIP = 0x4;
    private const uint THB_FLAGS = 0x8;

    private const uint THBF_ENABLED = 0x0;
    private const uint THBF_DISABLED = 0x1;

    private const uint BTN_PREV = 0;
    private const uint BTN_PLAY_PAUSE = 1;
    private const uint BTN_NEXT = 2;

    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;
    private IntPtr _iconPrev, _iconPlay, _iconPause, _iconNext;
    private bool _isPlaying;
    private bool _prevEnabled;
    private bool _nextEnabled;
    private bool _initialized;
    private SubclassProc? _subclassProc;

    internal event Action? PlayPauseRequested;
    internal event Action? NextRequested;
    internal event Action? PreviousRequested;

    internal bool Initialize(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            _hwnd = hwnd;

            _taskbar = (ITaskbarList3)new TaskbarListClass();
            int hr = _taskbar.HrInit();
            if (hr < 0) return false;

            int iconSize = ThumbBarNativeMethods.GetSystemMetrics(ThumbBarNativeMethods.SM_CXSMICON);
            if (iconSize <= 0) iconSize = 16;

            _iconPrev = CreatePrevIcon(iconSize);
            _iconPlay = CreatePlayIcon(iconSize);
            _iconPause = CreatePauseIcon(iconSize);
            _iconNext = CreateNextIcon(iconSize);

            // Subclass to catch WM_COMMAND for button clicks
            _subclassProc = SubclassWndProc;
            ThumbBarNativeMethods.SetWindowSubclass(_hwnd, _subclassProc, 1, 0);

            // Add 3 buttons: Previous | Play/Pause | Next
            var buttons = new THUMBBUTTON[]
            {
                new() { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = BTN_PREV, hIcon = _iconPrev, szTip = "Previous", dwFlags = THBF_DISABLED },
                new() { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = BTN_PLAY_PAUSE, hIcon = _iconPlay, szTip = "Play", dwFlags = THBF_ENABLED },
                new() { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = BTN_NEXT, hIcon = _iconNext, szTip = "Next", dwFlags = THBF_DISABLED },
            };

            hr = MarshalAndAddButtons(buttons);
            if (hr < 0) return false;

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void SetPlayingState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        UpdateAllButtons();
    }

    internal void SetNavigationEnabled(bool prev, bool next)
    {
        _prevEnabled = prev;
        _nextEnabled = next;
        UpdateAllButtons();
    }

    private void UpdateAllButtons()
    {
        if (!_initialized || _taskbar == null) return;

        try
        {
            var buttons = new THUMBBUTTON[]
            {
                new() { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = BTN_PREV, hIcon = _iconPrev, szTip = "Previous", dwFlags = _prevEnabled ? THBF_ENABLED : THBF_DISABLED },
                new() { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = BTN_PLAY_PAUSE, hIcon = _isPlaying ? _iconPause : _iconPlay, szTip = _isPlaying ? "Pause" : "Play", dwFlags = THBF_ENABLED },
                new() { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = BTN_NEXT, hIcon = _iconNext, szTip = "Next", dwFlags = _nextEnabled ? THBF_ENABLED : THBF_DISABLED },
            };
            MarshalAndUpdateButtons(buttons);
        }
        catch { }
    }

    #region Marshal Helpers

    private int MarshalAndAddButtons(THUMBBUTTON[] buttons)
    {
        int size = Marshal.SizeOf<THUMBBUTTON>();
        IntPtr ptr = Marshal.AllocHGlobal(size * buttons.Length);
        try
        {
            for (int i = 0; i < buttons.Length; i++)
                Marshal.StructureToPtr(buttons[i], ptr + i * size, false);
            return _taskbar!.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, ptr);
        }
        finally
        {
            for (int i = 0; i < buttons.Length; i++)
                Marshal.DestroyStructure<THUMBBUTTON>(ptr + i * size);
            Marshal.FreeHGlobal(ptr);
        }
    }

    private int MarshalAndUpdateButtons(THUMBBUTTON[] buttons)
    {
        int size = Marshal.SizeOf<THUMBBUTTON>();
        IntPtr ptr = Marshal.AllocHGlobal(size * buttons.Length);
        try
        {
            for (int i = 0; i < buttons.Length; i++)
                Marshal.StructureToPtr(buttons[i], ptr + i * size, false);
            return _taskbar!.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, ptr);
        }
        finally
        {
            for (int i = 0; i < buttons.Length; i++)
                Marshal.DestroyStructure<THUMBBUTTON>(ptr + i * size);
            Marshal.FreeHGlobal(ptr);
        }
    }

    #endregion

    #region WndProc Subclass

    private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_COMMAND)
        {
            int hiWord = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            int loWord = (int)(wParam.ToInt64() & 0xFFFF);

            if (hiWord == THBN_CLICKED)
            {
                switch ((uint)loWord)
                {
                    case BTN_PREV:
                        PreviousRequested?.Invoke();
                        return IntPtr.Zero;
                    case BTN_PLAY_PAUSE:
                        PlayPauseRequested?.Invoke();
                        return IntPtr.Zero;
                    case BTN_NEXT:
                        NextRequested?.Invoke();
                        return IntPtr.Zero;
                }
            }
        }

        return ThumbBarNativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    #endregion

    #region Icon Creation

    private static IntPtr CreatePlayIcon(int size)
    {
        return CreateArgbIcon(size, (pixels, s) =>
        {
            // Right-pointing triangle ▶
            float left = s * 0.28f;
            float right = s * 0.78f;
            float cy = s * 0.5f;
            float halfH = s * 0.32f;

            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dy = Math.Abs(y - cy);
                    if (dy > halfH) continue;
                    float maxX = left + (right - left) * (1.0f - dy / halfH);
                    if (x >= left && x <= maxX)
                        pixels[y * s + x] = 0xFFFFFFFF;
                }
        });
    }

    private static IntPtr CreatePauseIcon(int size)
    {
        return CreateArgbIcon(size, (pixels, s) =>
        {
            // Two vertical bars ❚❚
            int top = (int)(s * 0.2f);
            int bottom = (int)(s * 0.8f);
            int bar1Left = (int)(s * 0.25f);
            int bar1Right = (int)(s * 0.42f);
            int bar2Left = (int)(s * 0.58f);
            int bar2Right = (int)(s * 0.75f);

            for (int y = top; y <= bottom; y++)
                for (int x = 0; x < s; x++)
                {
                    if ((x >= bar1Left && x <= bar1Right) ||
                        (x >= bar2Left && x <= bar2Right))
                        pixels[y * s + x] = 0xFFFFFFFF;
                }
        });
    }

    private static IntPtr CreatePrevIcon(int size)
    {
        return CreateArgbIcon(size, (pixels, s) =>
        {
            // Bar + left-pointing triangle |◀
            int top = (int)(s * 0.2f);
            int bottom = (int)(s * 0.8f);
            int barLeft = (int)(s * 0.12f);
            int barRight = (int)(s * 0.22f);

            // Bar
            for (int y = top; y <= bottom; y++)
                for (int x = barLeft; x <= barRight; x++)
                    pixels[y * s + x] = 0xFFFFFFFF;

            // Left-pointing triangle
            float triRight = s * 0.82f;
            float triLeft = s * 0.30f;
            float cy = s * 0.5f;
            float halfH = s * 0.32f;

            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dy = Math.Abs(y - cy);
                    if (dy > halfH) continue;
                    float minX = triRight - (triRight - triLeft) * (1.0f - dy / halfH);
                    if (x >= minX && x <= triRight)
                        pixels[y * s + x] = 0xFFFFFFFF;
                }
        });
    }

    private static IntPtr CreateNextIcon(int size)
    {
        return CreateArgbIcon(size, (pixels, s) =>
        {
            // Right-pointing triangle + bar ▶|
            float triLeft = s * 0.18f;
            float triRight = s * 0.70f;
            float cy = s * 0.5f;
            float halfH = s * 0.32f;

            // Triangle
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dy = Math.Abs(y - cy);
                    if (dy > halfH) continue;
                    float maxX = triLeft + (triRight - triLeft) * (1.0f - dy / halfH);
                    if (x >= triLeft && x <= maxX)
                        pixels[y * s + x] = 0xFFFFFFFF;
                }

            // Bar
            int top = (int)(s * 0.2f);
            int bottom = (int)(s * 0.8f);
            int barLeft = (int)(s * 0.78f);
            int barRight = (int)(s * 0.88f);

            for (int y = top; y <= bottom; y++)
                for (int x = barLeft; x <= barRight; x++)
                    pixels[y * s + x] = 0xFFFFFFFF;
        });
    }

    private static IntPtr CreateArgbIcon(int size, Action<uint[], int> draw)
    {
        var pixels = new uint[size * size];
        draw(pixels, size);

        var colorHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        IntPtr colorBmp = IntPtr.Zero, maskBmp = IntPtr.Zero;

        try
        {
            colorBmp = ThumbBarNativeMethods.CreateBitmap(size, size, 1, 32, colorHandle.AddrOfPinnedObject());

            // Mask must be all-zero for 32-bit ARGB alpha to be used
            int maskStride = ((size + 15) / 16) * 2;
            var maskBits = new byte[maskStride * size];
            var maskHandle = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
            try
            {
                maskBmp = ThumbBarNativeMethods.CreateBitmap(size, size, 1, 1, maskHandle.AddrOfPinnedObject());
            }
            finally
            {
                maskHandle.Free();
            }

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = maskBmp,
                hbmColor = colorBmp,
            };

            return ThumbBarNativeMethods.CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            colorHandle.Free();
            if (colorBmp != IntPtr.Zero) ThumbBarNativeMethods.DeleteObject(colorBmp);
            if (maskBmp != IntPtr.Zero) ThumbBarNativeMethods.DeleteObject(maskBmp);
        }
    }

    #endregion

    public void Dispose()
    {
        if (!_initialized) return;
        _initialized = false;

        if (_subclassProc != null && _hwnd != IntPtr.Zero)
        {
            ThumbBarNativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, 1);
            _subclassProc = null;
        }

        if (_taskbar != null)
        {
            Marshal.ReleaseComObject(_taskbar);
            _taskbar = null;
        }

        DestroyIconSafe(ref _iconPrev);
        DestroyIconSafe(ref _iconPlay);
        DestroyIconSafe(ref _iconPause);
        DestroyIconSafe(ref _iconNext);
    }

    private static void DestroyIconSafe(ref IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            ThumbBarNativeMethods.DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
    }
}
