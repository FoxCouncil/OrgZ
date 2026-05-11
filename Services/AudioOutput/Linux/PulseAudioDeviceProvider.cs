// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.AudioOutput.Linux;

/// <summary>
/// Enumerates PulseAudio sinks via the async API and constructs
/// <see cref="PulseAudioSink"/>s for them.  Supported whenever <c>libpulse</c>
/// is present on the system (near-universal on modern Linux desktops; also
/// satisfied by PipeWire's Pulse compatibility shim).
/// </summary>
internal sealed class PulseAudioDeviceProvider : IAudioSinkProvider
{
    public const string Id = "pulseaudio";

    private static readonly ILogger _log = Logging.For("PulseAudioDeviceProvider");

    public string ProviderId => Id;
    public string ProviderName => "PulseAudio";
    public bool IsSupported => OperatingSystem.IsLinux();

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (!IsSupported)
        {
            return [];
        }

        var result = new List<AudioDeviceInfo>
        {
            // Synthetic default entry so users have a "follow the system" option
            // even if full enumeration fails (e.g., unusual container environments).
            new AudioDeviceInfo
            {
                DeviceId = "default",
                DisplayName = "Default PulseAudio Sink",
                ProviderId = Id,
                ProviderName = ProviderName,
                IsDefault = true,
            },
        };

        IntPtr mainloop = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;

        try
        {
            mainloop = PulseNative.pa_threaded_mainloop_new();
            if (mainloop == IntPtr.Zero)
            {
                return result;
            }

            PulseNative.pa_threaded_mainloop_start(mainloop);
            PulseNative.pa_threaded_mainloop_lock(mainloop);

            var api = PulseNative.pa_threaded_mainloop_get_api(mainloop);
            context = PulseNative.pa_context_new(api, "OrgZ-Enumerator");

            var stateCallback = new PulseNative.pa_context_notify_cb((c, ud) =>
            {
                PulseNative.pa_threaded_mainloop_signal(mainloop, 0);
            });
            PulseNative.pa_context_set_state_callback(context, stateCallback, IntPtr.Zero);
            PulseNative.pa_context_connect(context, null, 0, IntPtr.Zero);

            // Wait for context to be ready (or failed).
            while (true)
            {
                var state = PulseNative.pa_context_get_state(context);
                if (state == PulseNative.PA_CONTEXT_READY)
                {
                    break;
                }
                if (state == PulseNative.PA_CONTEXT_FAILED || state == PulseNative.PA_CONTEXT_TERMINATED)
                {
                    return result;
                }
                PulseNative.pa_threaded_mainloop_wait(mainloop);
            }

            var sinks = new List<(string Name, string Description)>();
            var sinkCallback = new PulseNative.pa_sink_info_cb((c, info, eol, ud) =>
            {
                if (eol > 0 || info == IntPtr.Zero)
                {
                    PulseNative.pa_threaded_mainloop_signal(mainloop, 0);
                    return;
                }

                var sinkInfo = Marshal.PtrToStructure<PulseNative.pa_sink_info>(info);
                var name = Marshal.PtrToStringUTF8(sinkInfo.name) ?? string.Empty;
                var description = Marshal.PtrToStringUTF8(sinkInfo.description) ?? name;
                sinks.Add((name, description));
            });

            var op = PulseNative.pa_context_get_sink_info_list(context, sinkCallback, IntPtr.Zero);
            if (op != IntPtr.Zero)
            {
                PulseNative.pa_threaded_mainloop_wait(mainloop);
                PulseNative.pa_operation_unref(op);
            }

            foreach (var (name, description) in sinks)
            {
                result.Add(new AudioDeviceInfo
                {
                    DeviceId = name,
                    DisplayName = description,
                    ProviderId = Id,
                    ProviderName = ProviderName,
                });
            }
        }
        catch (DllNotFoundException)
        {
            _log.Debug("libpulse not found — PulseAudio provider disabled");
            return [];
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "PulseAudio enumeration failed; returning default only");
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                PulseNative.pa_context_disconnect(context);
                PulseNative.pa_context_unref(context);
            }
            if (mainloop != IntPtr.Zero)
            {
                PulseNative.pa_threaded_mainloop_unlock(mainloop);
                PulseNative.pa_threaded_mainloop_stop(mainloop);
                PulseNative.pa_threaded_mainloop_free(mainloop);
            }
        }

        return result;
    }

    public IAudioSink CreateSink(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var deviceName = device.DeviceId == "default" ? null : device.DeviceId;
        return new PulseAudioSink(device.QualifiedId, device.DisplayName, deviceName);
    }

    internal void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
