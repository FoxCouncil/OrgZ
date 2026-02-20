// Copyright (c) 2025 Fox Diller

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace OrgZ.Services;

#region Enums

internal enum MediaPlaybackStatus
{
    Closed = 0,
    Changing = 1,
    Stopped = 2,
    Playing = 3,
    Paused = 4
}

internal enum MediaPlaybackType
{
    Unknown = 0,
    Music = 1,
    Video = 2,
    Image = 3
}

internal enum SmtcButton
{
    Play = 0,
    Pause = 1,
    Stop = 2,
    Record = 3,
    FastForward = 4,
    Rewind = 5,
    Next = 6,
    Previous = 7,
    ChannelUp = 8,
    ChannelDown = 9
}

#endregion

#region COM Interfaces

[ComImport, Guid("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISystemMediaTransportControlsInterop
{
    // IInspectable vtable padding (slots 3-5)
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    [PreserveSig]
    int GetForWindow(IntPtr appWindow, ref Guid riid, out IntPtr result);
}

[ComImport, Guid("99FA3FF4-1742-42A6-902E-087D41F965EC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISystemMediaTransportControls
{
    // IInspectable vtable padding (slots 3-5)
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    // Slots 6-7: PlaybackStatus
    [PreserveSig] int get_PlaybackStatus(out int value);
    [PreserveSig] int put_PlaybackStatus(int value);
    // Slot 8: DisplayUpdater
    [PreserveSig] int get_DisplayUpdater(out IntPtr value);
    // Slot 9: SoundLevel
    [PreserveSig] int get_SoundLevel(out int value);
    // Slots 10-11: IsEnabled
    [PreserveSig] int get_IsEnabled(out byte value);
    [PreserveSig] int put_IsEnabled(byte value);
    // Slots 12-13: IsPlayEnabled
    [PreserveSig] int get_IsPlayEnabled(out byte value);
    [PreserveSig] int put_IsPlayEnabled(byte value);
    // Slots 14-15: IsStopEnabled (SDK order: Stop before Pause)
    [PreserveSig] int get_IsStopEnabled(out byte value);
    [PreserveSig] int put_IsStopEnabled(byte value);
    // Slots 16-17: IsPauseEnabled
    [PreserveSig] int get_IsPauseEnabled(out byte value);
    [PreserveSig] int put_IsPauseEnabled(byte value);
    // Slots 18-19: IsRecordEnabled
    [PreserveSig] int get_IsRecordEnabled(out byte value);
    [PreserveSig] int put_IsRecordEnabled(byte value);
    // Slots 20-21: IsFastForwardEnabled
    [PreserveSig] int get_IsFastForwardEnabled(out byte value);
    [PreserveSig] int put_IsFastForwardEnabled(byte value);
    // Slots 22-23: IsRewindEnabled
    [PreserveSig] int get_IsRewindEnabled(out byte value);
    [PreserveSig] int put_IsRewindEnabled(byte value);
    // Slots 24-25: IsPreviousEnabled
    [PreserveSig] int get_IsPreviousEnabled(out byte value);
    [PreserveSig] int put_IsPreviousEnabled(byte value);
    // Slots 26-27: IsNextEnabled
    [PreserveSig] int get_IsNextEnabled(out byte value);
    [PreserveSig] int put_IsNextEnabled(byte value);
    // Slots 28-29: IsChannelUpEnabled
    [PreserveSig] int get_IsChannelUpEnabled(out byte value);
    [PreserveSig] int put_IsChannelUpEnabled(byte value);
    // Slots 30-31: IsChannelDownEnabled
    [PreserveSig] int get_IsChannelDownEnabled(out byte value);
    [PreserveSig] int put_IsChannelDownEnabled(byte value);
    // Slots 32-33: ButtonPressed event
    [PreserveSig] int add_ButtonPressed(IntPtr handler, out long token);
    [PreserveSig] int remove_ButtonPressed(long token);
}

[ComImport, Guid("8ABBC53E-FA55-4ECF-AD8E-C984E5DD1550"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISmtcDisplayUpdater
{
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    // Slots 6-7: Type
    [PreserveSig] int get_Type(out int value);
    [PreserveSig] int put_Type(int value);
    // Slots 8-9: AppMediaId
    [PreserveSig] int get_AppMediaId(out IntPtr value);
    [PreserveSig] int put_AppMediaId(IntPtr value);
    // Slots 10-11: Thumbnail
    [PreserveSig] int get_Thumbnail(out IntPtr value);
    [PreserveSig] int put_Thumbnail(IntPtr value);
    // Slot 12: MusicProperties
    [PreserveSig] int get_MusicProperties(out IntPtr value);
    // Slot 13: VideoProperties
    [PreserveSig] int get_VideoProperties(out IntPtr value);
    // Slot 14: ImageProperties
    [PreserveSig] int get_ImageProperties(out IntPtr value);
    // Slot 15: CopyFromFileAsync
    [PreserveSig] int CopyFromFileAsync(int type, IntPtr source, out IntPtr operation);
    // Slot 16: ClearAll
    [PreserveSig] int ClearAll();
    // Slot 17: Update
    [PreserveSig] int Update();
}

[ComImport, Guid("6BBF0C59-D0A0-4D26-92A0-F978E1D18E7B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMusicDisplayProperties
{
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    [PreserveSig] int get_Title(out IntPtr value);
    [PreserveSig] int put_Title(IntPtr value);
    [PreserveSig] int get_AlbumArtist(out IntPtr value);
    [PreserveSig] int put_AlbumArtist(IntPtr value);
    [PreserveSig] int get_Artist(out IntPtr value);
    [PreserveSig] int put_Artist(IntPtr value);
}

[ComImport, Guid("00368462-97D3-44B9-B00F-008AFCEFAF18"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMusicDisplayProperties2
{
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    [PreserveSig] int get_AlbumTitle(out IntPtr value);
    [PreserveSig] int put_AlbumTitle(IntPtr value);
}

[ComImport, Guid("B7F47116-A56F-4DC8-9E11-92031F4A87C2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISmtcButtonPressedEventArgs
{
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    [PreserveSig] int get_Button(out int value);
}

[ComImport, Guid("857309DC-3FBF-4E7D-986F-EF3B1A07A964"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRandomAccessStreamReferenceStatics
{
    [PreserveSig] int IInspectable_GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int IInspectable_GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int IInspectable_GetTrustLevel(out int trustLevel);

    [PreserveSig] int CreateFromFile(IntPtr file, out IntPtr streamRef);
    [PreserveSig] int CreateFromUri(IntPtr uri, out IntPtr streamRef);
    [PreserveSig] int CreateFromStream(IntPtr stream, out IntPtr streamRef);
}

#endregion

#region P/Invoke

internal static class SmtcNativeMethods
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    internal static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode)]
    internal static extern int WindowsCreateString(
        string sourceString, int length, out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("ole32.dll")]
    internal static extern int CreateStreamOnHGlobal(
        IntPtr hGlobal,
        [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease,
        out IStream ppstm);

    [DllImport("shcore.dll")]
    internal static extern int CreateRandomAccessStreamOverStream(
        IStream stream, uint options, ref Guid riid, out IntPtr ppv);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    internal static extern int RoInitialize(int initType);
}

#endregion

#region Native Event Handler

internal static unsafe class ButtonPressedHandlerFactory
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IID_TypedEventHandler = new("0557E996-7B23-5BAE-AA81-EA0D671143A4");

    private static Action<SmtcButton>? s_callback;
    private static IntPtr s_vtable;
    private static IntPtr s_instance;

    internal static IntPtr Create(Action<SmtcButton> callback)
    {
        s_callback = callback;

        if (s_vtable == IntPtr.Zero)
        {
            s_vtable = (IntPtr)NativeMemory.AllocZeroed((nuint)(4 * IntPtr.Size));
            ((IntPtr*)s_vtable)[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
            ((IntPtr*)s_vtable)[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
            ((IntPtr*)s_vtable)[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
            ((IntPtr*)s_vtable)[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&Invoke;
        }

        // COM object layout: [vtable_ptr][ref_count]
        s_instance = (IntPtr)NativeMemory.AllocZeroed((nuint)(IntPtr.Size + sizeof(int)));
        *(IntPtr*)s_instance = s_vtable;
        *(int*)(s_instance + IntPtr.Size) = 1;

        return s_instance;
    }

    internal static void Destroy()
    {
        s_callback = null;
        if (s_instance != IntPtr.Zero)
        {
            NativeMemory.Free((void*)s_instance);
            s_instance = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(IntPtr thisPtr, Guid* riid, IntPtr* ppv)
    {
        if (*riid == IID_IUnknown || *riid == IID_IAgileObject || *riid == IID_TypedEventHandler)
        {
            *ppv = thisPtr;
            Interlocked.Increment(ref *(int*)(thisPtr + IntPtr.Size));
            return 0; // S_OK
        }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(IntPtr thisPtr)
    {
        return (uint)Interlocked.Increment(ref *(int*)(thisPtr + IntPtr.Size));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(IntPtr thisPtr)
    {
        return (uint)Interlocked.Decrement(ref *(int*)(thisPtr + IntPtr.Size));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(IntPtr thisPtr, IntPtr sender, IntPtr args)
    {
        object? rcw = null;
        try
        {
            if (args != IntPtr.Zero && s_callback != null)
            {
                rcw = Marshal.GetObjectForIUnknown(args);
                var eventArgs = (ISmtcButtonPressedEventArgs)rcw;
                eventArgs.get_Button(out int button);
                s_callback((SmtcButton)button);
            }
            return 0;
        }
        catch
        {
            return unchecked((int)0x80004005); // E_FAIL
        }
        finally
        {
            if (rcw != null)
                Marshal.ReleaseComObject(rcw);
        }
    }
}

#endregion

internal sealed class SmtcService : IDisposable
{
    private static readonly Guid IID_IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");

    private ISystemMediaTransportControls? _smtc;
    private long _buttonPressedToken;
    private bool _initialized;

    internal event Action? PlayPauseRequested;
    internal event Action? NextRequested;
    internal event Action? PreviousRequested;

    internal string? InitDiagnostics { get; private set; }

    internal bool Initialize(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            InitDiagnostics = "SMTC: Not Windows 10+";
            return false;
        }

        try
        {
            // Ensure WinRT is initialized on this thread
            int roHr = SmtcNativeMethods.RoInitialize(0); // RO_INIT_SINGLETHREADED
            System.Diagnostics.Debug.WriteLine($"SMTC: RoInitialize hr=0x{roHr:X8}");

            int hr = SmtcNativeMethods.WindowsCreateString(
                "Windows.Media.SystemMediaTransportControls",
                "Windows.Media.SystemMediaTransportControls".Length,
                out IntPtr hClassName);
            if (hr < 0)
            {
                InitDiagnostics = $"SMTC: WindowsCreateString failed hr=0x{hr:X8}";
                return false;
            }

            try
            {
                var interopIid = typeof(ISystemMediaTransportControlsInterop).GUID;
                hr = SmtcNativeMethods.RoGetActivationFactory(hClassName, ref interopIid, out IntPtr factoryPtr);
                System.Diagnostics.Debug.WriteLine($"SMTC: RoGetActivationFactory hr=0x{hr:X8} ptr=0x{factoryPtr:X}");
                if (hr < 0)
                {
                    InitDiagnostics = $"SMTC: RoGetActivationFactory failed hr=0x{hr:X8}";
                    return false;
                }

                try
                {
                    var interop = (ISystemMediaTransportControlsInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                    try
                    {
                        var smtcIid = typeof(ISystemMediaTransportControls).GUID;
                        System.Diagnostics.Debug.WriteLine($"SMTC: GetForWindow hwnd=0x{hwnd:X} smtcIid={smtcIid}");
                        hr = interop.GetForWindow(hwnd, ref smtcIid, out IntPtr smtcPtr);
                        System.Diagnostics.Debug.WriteLine($"SMTC: GetForWindow hr=0x{hr:X8} ptr=0x{smtcPtr:X}");
                        if (hr < 0)
                        {
                            InitDiagnostics = $"SMTC: GetForWindow failed hr=0x{hr:X8}";
                            return false;
                        }

                        _smtc = (ISystemMediaTransportControls)Marshal.GetObjectForIUnknown(smtcPtr);
                        Marshal.Release(smtcPtr);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(interop);
                    }
                }
                finally
                {
                    Marshal.Release(factoryPtr);
                }
            }
            finally
            {
                SmtcNativeMethods.WindowsDeleteString(hClassName);
            }

            hr = _smtc.put_IsEnabled(1);
            System.Diagnostics.Debug.WriteLine($"SMTC: put_IsEnabled hr=0x{hr:X8}");
            _smtc.put_IsPlayEnabled(1);
            _smtc.put_IsPauseEnabled(1);
            _smtc.put_IsNextEnabled(0);
            _smtc.put_IsPreviousEnabled(0);

            // Set initial playback status and call Update() on the display updater
            // so Windows registers our SMTC session (required per MSDN docs)
            hr = _smtc.put_PlaybackStatus((int)MediaPlaybackStatus.Closed);
            System.Diagnostics.Debug.WriteLine($"SMTC: put_PlaybackStatus(Closed) hr=0x{hr:X8}");

            hr = _smtc.get_DisplayUpdater(out IntPtr initUpdaterPtr);
            System.Diagnostics.Debug.WriteLine($"SMTC: get_DisplayUpdater hr=0x{hr:X8} ptr=0x{initUpdaterPtr:X}");
            if (hr >= 0 && initUpdaterPtr != IntPtr.Zero)
            {
                var initUpdater = (ISmtcDisplayUpdater)Marshal.GetObjectForIUnknown(initUpdaterPtr);
                Marshal.Release(initUpdaterPtr);
                try
                {
                    hr = initUpdater.put_Type((int)MediaPlaybackType.Music);
                    System.Diagnostics.Debug.WriteLine($"SMTC: put_Type(Music) hr=0x{hr:X8}");
                    hr = initUpdater.Update();
                    System.Diagnostics.Debug.WriteLine($"SMTC: Update() hr=0x{hr:X8}");
                }
                finally
                {
                    Marshal.ReleaseComObject(initUpdater);
                }
            }

            var handlerPtr = ButtonPressedHandlerFactory.Create(OnButtonPressed);
            hr = _smtc.add_ButtonPressed(handlerPtr, out _buttonPressedToken);
            System.Diagnostics.Debug.WriteLine($"SMTC: add_ButtonPressed hr=0x{hr:X8} token={_buttonPressedToken}");
            if (hr < 0)
            {
                ButtonPressedHandlerFactory.Destroy();
            }

            _initialized = true;
            InitDiagnostics = "SMTC: Initialized OK";
            System.Diagnostics.Debug.WriteLine("SMTC: Initialization complete");
            return true;
        }
        catch (Exception ex)
        {
            InitDiagnostics = $"SMTC: Exception: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"SMTC: Exception during init: {ex}");
            return false;
        }
    }

    internal void SetPlaybackStatus(MediaPlaybackStatus status)
    {
        if (!_initialized || _smtc == null)
        {
            return;
        }

        try
        {
            int hr = _smtc.put_PlaybackStatus((int)status);
            System.Diagnostics.Debug.WriteLine($"SMTC: SetPlaybackStatus({status}) hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SMTC: SetPlaybackStatus exception: {ex.Message}");
        }
    }

    internal void SetNavigationEnabled(bool prev, bool next)
    {
        if (!_initialized || _smtc == null)
        {
            return;
        }

        try
        {
            _smtc.put_IsPreviousEnabled(prev ? (byte)1 : (byte)0);
            _smtc.put_IsNextEnabled(next ? (byte)1 : (byte)0);
        }
        catch { }
    }

    internal void UpdateMetadata(string? title, string? artist, string? album, byte[]? artBytes)
    {
        if (!_initialized || _smtc == null)
        {
            return;
        }

        try
        {
            int hr = _smtc.get_DisplayUpdater(out IntPtr updaterPtr);
            if (hr < 0 || updaterPtr == IntPtr.Zero)
            {
                return;
            }

            var updater = (ISmtcDisplayUpdater)Marshal.GetObjectForIUnknown(updaterPtr);
            Marshal.Release(updaterPtr);

            try
            {
                updater.put_Type((int)MediaPlaybackType.Music);

                hr = updater.get_MusicProperties(out IntPtr musicPtr);
                if (hr >= 0 && musicPtr != IntPtr.Zero)
                {
                    var music = (IMusicDisplayProperties)Marshal.GetObjectForIUnknown(musicPtr);
                    Marshal.Release(musicPtr);

                    try
                    {
                        IntPtr hsTitle = IntPtr.Zero, hsArtist = IntPtr.Zero, hsAlbum = IntPtr.Zero;
                        try
                        {
                            if (!string.IsNullOrEmpty(title))
                                SmtcNativeMethods.WindowsCreateString(title, title.Length, out hsTitle);

                            if (!string.IsNullOrEmpty(artist))
                                SmtcNativeMethods.WindowsCreateString(artist, artist.Length, out hsArtist);

                            if (!string.IsNullOrEmpty(album))
                                SmtcNativeMethods.WindowsCreateString(album, album.Length, out hsAlbum);

                            music.put_Title(hsTitle);
                            music.put_Artist(hsArtist);

                            try
                            {
                                var music2 = (IMusicDisplayProperties2)music;
                                music2.put_AlbumTitle(hsAlbum);
                            }
                            catch { }
                        }
                        finally
                        {
                            if (hsTitle != IntPtr.Zero)
                                SmtcNativeMethods.WindowsDeleteString(hsTitle);
                            if (hsArtist != IntPtr.Zero)
                                SmtcNativeMethods.WindowsDeleteString(hsArtist);
                            if (hsAlbum != IntPtr.Zero)
                                SmtcNativeMethods.WindowsDeleteString(hsAlbum);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(music);
                    }
                }

                if (artBytes is { Length: > 0 })
                {
                    try { SetThumbnail(updater, artBytes); } catch { }
                }

                updater.Update();
            }
            finally
            {
                Marshal.ReleaseComObject(updater);
            }
        }
        catch { }
    }

    private static unsafe void SetThumbnail(ISmtcDisplayUpdater updater, byte[] artBytes)
    {
        int hr = SmtcNativeMethods.CreateStreamOnHGlobal(IntPtr.Zero, true, out IStream stream);
        if (hr < 0)
            return;

        try
        {
            int written = 0;
            stream.Write(artBytes, artBytes.Length, (IntPtr)(&written));
            stream.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);

            var rasIid = IID_IRandomAccessStream;
            hr = SmtcNativeMethods.CreateRandomAccessStreamOverStream(stream, 0, ref rasIid, out IntPtr rasPtr);
            if (hr < 0)
                return;

            try
            {
                hr = SmtcNativeMethods.WindowsCreateString(
                    "Windows.Storage.Streams.RandomAccessStreamReference",
                    "Windows.Storage.Streams.RandomAccessStreamReference".Length,
                    out IntPtr hClassName);
                if (hr < 0)
                    return;

                try
                {
                    var statsIid = typeof(IRandomAccessStreamReferenceStatics).GUID;
                    hr = SmtcNativeMethods.RoGetActivationFactory(hClassName, ref statsIid, out IntPtr statsPtr);
                    if (hr < 0)
                        return;

                    var stats = (IRandomAccessStreamReferenceStatics)Marshal.GetObjectForIUnknown(statsPtr);
                    try
                    {
                        hr = stats.CreateFromStream(rasPtr, out IntPtr streamRefPtr);
                        if (hr >= 0 && streamRefPtr != IntPtr.Zero)
                        {
                            updater.put_Thumbnail(streamRefPtr);
                            Marshal.Release(streamRefPtr);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(stats);
                        Marshal.Release(statsPtr);
                    }
                }
                finally
                {
                    SmtcNativeMethods.WindowsDeleteString(hClassName);
                }
            }
            finally
            {
                Marshal.Release(rasPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    private void OnButtonPressed(SmtcButton button)
    {
        switch (button)
        {
            case SmtcButton.Play:
            case SmtcButton.Pause:
            {
                PlayPauseRequested?.Invoke();
            }
            break;

            case SmtcButton.Next:
            {
                NextRequested?.Invoke();
            }
            break;

            case SmtcButton.Previous:
            {
                PreviousRequested?.Invoke();
            }
            break;
        }
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;

        try
        {
            if (_smtc != null && _buttonPressedToken != 0)
            {
                _smtc.remove_ButtonPressed(_buttonPressedToken);
            }
        }
        catch { }

        ButtonPressedHandlerFactory.Destroy();

        if (_smtc != null)
        {
            Marshal.ReleaseComObject(_smtc);
            _smtc = null;
        }
    }
}
