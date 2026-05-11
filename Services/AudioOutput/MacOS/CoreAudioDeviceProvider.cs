// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.AudioOutput.MacOS;

/// <summary>
/// Enumerates macOS audio output devices via CoreAudio's AudioObject property
/// queries.  Devices with at least one output stream are listed.
/// </summary>
internal sealed class CoreAudioDeviceProvider : IAudioSinkProvider
{
    public const string Id = "coreaudio";

    private static readonly ILogger _log = Logging.For("CoreAudioDeviceProvider");

    public string ProviderId => Id;
    public string ProviderName => "Core Audio";
    public bool IsSupported => OperatingSystem.IsMacOS();

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var result = new List<AudioDeviceInfo>
        {
            new AudioDeviceInfo
            {
                DeviceId = "default",
                DisplayName = "Default Audio Output",
                ProviderId = Id,
                ProviderName = ProviderName,
                IsDefault = true,
            },
        };

        if (!IsSupported)
        {
            return result;
        }

        try
        {
            var addr = new CoreAudioNative.AudioObjectPropertyAddress
            {
                mSelector = CoreAudioNative.kAudioHardwarePropertyDevices,
                mScope = CoreAudioNative.kAudioObjectPropertyScopeGlobal,
                mElement = CoreAudioNative.kAudioObjectPropertyElementMain,
            };

            if (CoreAudioNative.AudioObjectGetPropertyDataSize(CoreAudioNative.kAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, out var size) != 0)
            {
                return result;
            }

            int deviceCount = (int)(size / sizeof(uint));
            if (deviceCount <= 0)
            {
                return result;
            }

            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (CoreAudioNative.AudioObjectGetPropertyData(CoreAudioNative.kAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, ref size, buf) != 0)
                {
                    return result;
                }

                for (int i = 0; i < deviceCount; i++)
                {
                    var deviceId = (uint)Marshal.ReadInt32(buf, i * sizeof(uint));
                    if (!DeviceHasOutput(deviceId))
                    {
                        continue;
                    }

                    var name = GetStringProperty(deviceId, CoreAudioNative.kAudioObjectPropertyName) ?? $"Audio Device {deviceId}";
                    var uid = GetStringProperty(deviceId, CoreAudioNative.kAudioDevicePropertyDeviceUID) ?? deviceId.ToString();

                    result.Add(new AudioDeviceInfo
                    {
                        DeviceId = uid,
                        DisplayName = name,
                        ProviderId = Id,
                        ProviderName = ProviderName,
                    });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (DllNotFoundException)
        {
            _log.Debug("CoreAudio.framework not loadable — provider disabled");
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "CoreAudio enumeration failed; returning default only");
        }

        return result;
    }

    private static bool DeviceHasOutput(uint deviceId)
    {
        var addr = new CoreAudioNative.AudioObjectPropertyAddress
        {
            mSelector = CoreAudioNative.kAudioDevicePropertyStreams,
            mScope = CoreAudioNative.kAudioDevicePropertyScopeOutput,
            mElement = CoreAudioNative.kAudioObjectPropertyElementMain,
        };

        if (CoreAudioNative.AudioObjectGetPropertyDataSize(deviceId, ref addr, 0, IntPtr.Zero, out var size) != 0)
        {
            return false;
        }

        return size > 0;
    }

    private static string? GetStringProperty(uint objectId, uint selector)
    {
        var addr = new CoreAudioNative.AudioObjectPropertyAddress
        {
            mSelector = selector,
            mScope = CoreAudioNative.kAudioObjectPropertyScopeGlobal,
            mElement = CoreAudioNative.kAudioObjectPropertyElementMain,
        };

        uint size = (uint)IntPtr.Size;
        var cfStringPtr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CoreAudioNative.AudioObjectGetPropertyData(objectId, ref addr, 0, IntPtr.Zero, ref size, cfStringPtr) != 0)
            {
                return null;
            }

            var cfStr = Marshal.ReadIntPtr(cfStringPtr);
            var managed = CoreAudioNative.CFStringToManaged(cfStr);
            if (cfStr != IntPtr.Zero)
            {
                CoreAudioNative.CFRelease(cfStr);
            }
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(cfStringPtr);
        }
    }

    public IAudioSink CreateSink(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var uid = device.DeviceId == "default" ? null : device.DeviceId;
        return new CoreAudioSink(device.QualifiedId, device.DisplayName, uid);
    }

    internal void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
