// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

#if WINDOWS
using System.Runtime.InteropServices;

namespace OrgZ.Services.AudioOutput.Windows;

/// <summary>
/// P/Invoke declarations for the Win32 waveOut API (winmm.dll).  Internal to
/// the <see cref="WaveOutSink"/> and <see cref="WaveOutDeviceProvider"/>
/// - kept in one file so the unmanaged surface area is easy to audit.
/// </summary>
internal static class WaveNative
{
    public const uint WAVE_MAPPER = 0xFFFFFFFFu;
    public const uint CALLBACK_NULL = 0;
    public const uint MMSYSERR_NOERROR = 0;
    public const uint WHDR_DONE = 0x00000001;
    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    public const int MAXPNAMELEN = 32;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    /// <summary>
    /// WAVEFORMATEXTENSIBLE - required for PCM above 16 bits per sample.
    /// Plain WAVEFORMATEX with wBitsPerSample=24/32 is rejected by many
    /// drivers; the extensible form with KSDATAFORMAT_SUBTYPE_PCM is the
    /// documented route for hi-res output through waveOut.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    public const uint SPEAKER_FRONT_LEFT = 0x1;
    public const uint SPEAKER_FRONT_RIGHT = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAXPNAMELEN)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEXTENSIBLE pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutPause(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutRestart(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutClose(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutSetVolume(IntPtr hwo, uint dwVolume);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
    public static extern uint waveOutGetDevCaps(IntPtr uDeviceID, out WAVEOUTCAPS pwoc, uint cbwoc);
}
#endif
