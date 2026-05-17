// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;

namespace OrgZ.Services.AudioOutput.MacOS;

/// <summary>
/// P/Invoke declarations for Apple's CoreAudio and AudioToolbox frameworks.
/// Only compiled in cross-platform so static analysis runs everywhere; the
/// actual calls are guarded by <c>OperatingSystem.IsMacOS()</c> at the
/// provider level.
/// </summary>
/// <remarks>
/// <para>
/// We use two APIs: CoreAudio's AudioObject property queries for device
/// enumeration, and AudioToolbox's AudioQueue for actual playback.  The
/// AudioQueue abstraction is simpler than AUHAL for a stereo-output
/// scenario and lets us target a specific device by UID via
/// <c>kAudioQueueProperty_CurrentDevice</c>.
/// </para>
/// </remarks>
internal static class CoreAudioNative
{
    public const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    public const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint kAudioObjectSystemObject = 1;
    public const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62;          // 'glob'
    public const uint kAudioObjectPropertyElementMain = 0;
    public const uint kAudioHardwarePropertyDevices = 0x64657623;            // 'dev#'
    public const uint kAudioObjectPropertyName = 0x6C6E616D;                 // 'lnam'
    public const uint kAudioDevicePropertyDeviceUID = 0x75696420;            // 'uid '
    public const uint kAudioDevicePropertyStreams = 0x73746D23;              // 'stm#'
    public const uint kAudioDevicePropertyStreamConfiguration = 0x736C6179;  // 'slay'
    public const uint kAudioDevicePropertyScopeOutput = 0x6F757470;          // 'outp'

    public const uint kAudioFormatLinearPCM = 0x6C70636D;                    // 'lpcm'
    public const uint kLinearPCMFormatFlagIsSignedInteger = 0x4;
    public const uint kLinearPCMFormatFlagIsPacked = 0x8;

    public const uint kAudioQueueProperty_CurrentDevice = 0x61716364;        // 'aqcd'

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress
    {
        public uint mSelector;
        public uint mScope;
        public uint mElement;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioQueueBuffer
    {
        public uint mAudioDataBytesCapacity;
        public IntPtr mAudioData;
        public uint mAudioDataByteSize;
        public IntPtr mUserData;
        public uint mPacketDescriptionCapacity;
        public IntPtr mPacketDescriptions;
        public uint mPacketDescriptionCount;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr audioQueue, IntPtr buffer);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    public static extern int AudioObjectGetPropertyData(uint inObjectID, ref AudioObjectPropertyAddress inAddress, uint inQualifierDataSize, IntPtr inQualifierData, ref uint ioDataSize, IntPtr outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyDataSize")]
    public static extern int AudioObjectGetPropertyDataSize(uint inObjectID, ref AudioObjectPropertyAddress inAddress, uint inQualifierDataSize, IntPtr inQualifierData, out uint outDataSize);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueNewOutput")]
    public static extern int AudioQueueNewOutput(ref AudioStreamBasicDescription inFormat, AudioQueueOutputCallback inCallbackProc, IntPtr inUserData, IntPtr inCallbackRunLoop, IntPtr inCallbackRunLoopMode, uint inFlags, out IntPtr outAudioQueue);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueDispose")]
    public static extern int AudioQueueDispose(IntPtr inAudioQueue, bool inImmediate);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueAllocateBuffer")]
    public static extern int AudioQueueAllocateBuffer(IntPtr inAudioQueue, uint inBufferByteSize, out IntPtr outBuffer);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueEnqueueBuffer")]
    public static extern int AudioQueueEnqueueBuffer(IntPtr inAudioQueue, IntPtr inBuffer, uint inNumPacketDescs, IntPtr inPacketDescs);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueStart")]
    public static extern int AudioQueueStart(IntPtr inAudioQueue, IntPtr inStartTime);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueStop")]
    public static extern int AudioQueueStop(IntPtr inAudioQueue, bool inImmediate);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueReset")]
    public static extern int AudioQueueReset(IntPtr inAudioQueue);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueuePause")]
    public static extern int AudioQueuePause(IntPtr inAudioQueue);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueSetProperty")]
    public static extern int AudioQueueSetProperty(IntPtr inAudioQueue, uint inID, IntPtr inData, uint inDataSize);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueSetParameter")]
    public static extern int AudioQueueSetParameter(IntPtr inAudioQueue, uint inParamID, float inValue);

    public const uint kAudioQueueParam_Volume = 1;

    [DllImport(CoreFoundation, EntryPoint = "CFStringGetCStringPtr")]
    public static extern IntPtr CFStringGetCStringPtr(IntPtr theString, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFStringGetCString")]
    public static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFStringGetLength")]
    public static extern long CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundation, EntryPoint = "CFRelease")]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    public static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

    public const uint kCFStringEncodingUTF8 = 0x08000100;

    public static string? CFStringToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero)
        {
            return null;
        }

        var ptr = CFStringGetCStringPtr(cfString, kCFStringEncodingUTF8);
        if (ptr != IntPtr.Zero)
        {
            return Marshal.PtrToStringUTF8(ptr);
        }

        var length = CFStringGetLength(cfString);
        var capacity = (length + 1) * 4;
        var buf = Marshal.AllocHGlobal((int)capacity);
        try
        {
            if (CFStringGetCString(cfString, buf, capacity, kCFStringEncodingUTF8))
            {
                return Marshal.PtrToStringUTF8(buf);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return null;
    }
}
