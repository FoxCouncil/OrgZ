// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

#if WINDOWS
using System.Runtime.InteropServices;

namespace OrgZ.Services.AudioOutput.Windows;

/// <summary>
/// Enumerates Win32 waveOut devices via <c>waveOutGetNumDevs</c> +
/// <c>waveOutGetDevCapsW</c> and constructs <see cref="WaveOutSink"/>s.
/// Included in OrgZ on every Windows build; <see cref="IsSupported"/> is
/// always <c>true</c> here because waveOut ships with Windows since 3.1.
/// </summary>
internal sealed class WaveOutDeviceProvider : IAudioSinkProvider
{
    public const string Id = "waveout";

    public string ProviderId => Id;
    public string ProviderName => "Windows Wave Out";
    public bool IsSupported => OperatingSystem.IsWindows();

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var count = WaveNative.waveOutGetNumDevs();
        var list = new List<AudioDeviceInfo>((int)count + 1);

        // Index 0 gets a synthetic "Default" entry from WAVE_MAPPER so users
        // have a stable target that follows the Windows system-default device.
        // waveOutGetDevCaps(WAVE_MAPPER) returns the current default's caps.
        if (WaveNative.waveOutGetDevCaps(unchecked((IntPtr)(int)WaveNative.WAVE_MAPPER), out var defCaps, (uint)Marshal.SizeOf<WaveNative.WAVEOUTCAPS>()) == WaveNative.MMSYSERR_NOERROR)
        {
            list.Add(new AudioDeviceInfo
            {
                DeviceId = "default",
                DisplayName = $"Default — {defCaps.szPname.TrimEnd('\0')}",
                ProviderId = Id,
                ProviderName = ProviderName,
                IsDefault = true,
            });
        }

        for (uint i = 0; i < count; i++)
        {
            if (WaveNative.waveOutGetDevCaps((IntPtr)(int)i, out var caps, (uint)Marshal.SizeOf<WaveNative.WAVEOUTCAPS>()) != WaveNative.MMSYSERR_NOERROR)
            {
                continue;
            }

            list.Add(new AudioDeviceInfo
            {
                DeviceId = i.ToString(),
                DisplayName = caps.szPname.TrimEnd('\0'),
                ProviderId = Id,
                ProviderName = ProviderName,
                IsDefault = false,
            });
        }

        return list;
    }

    public IAudioSink CreateSink(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.ProviderId != Id)
        {
            throw new ArgumentException($"Device provider mismatch: expected {Id}, got {device.ProviderId}");
        }

        uint deviceId = device.DeviceId == "default"
            ? WaveNative.WAVE_MAPPER
            : uint.Parse(device.DeviceId);

        return new WaveOutSink(deviceId, device.DisplayName, device.QualifiedId);
    }

    internal void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
#endif
