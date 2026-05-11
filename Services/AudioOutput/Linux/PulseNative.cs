// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;

namespace OrgZ.Services.AudioOutput.Linux;

/// <summary>
/// P/Invoke declarations for libpulse — covers both the "simple" API
/// (blocking playback) and enough of the async API to enumerate sinks.
/// Only compiled into Linux builds but kept free of <c>#if</c> so that
/// static analysis runs on it everywhere.
/// </summary>
internal static class PulseNative
{
    public const int PA_STREAM_PLAYBACK = 1;
    public const int PA_SAMPLE_S16LE = 3;

    public const int PA_CONTEXT_READY = 4;
    public const int PA_CONTEXT_FAILED = 5;
    public const int PA_CONTEXT_TERMINATED = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct pa_sample_spec
    {
        public int format;
        public uint rate;
        public byte channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct pa_sink_info
    {
        public IntPtr name;          // const char *
        public uint index;
        public IntPtr description;   // const char *
        // Remaining fields (sample_spec, channel_map, owner_module, ...) elided:
        // we only read name/description for the device list.  The struct is
        // fairly large and the remaining fields don't matter for enumeration;
        // crossing the marshaling boundary with IntPtr for the first two
        // pointers is safe because libpulse always puts them at offsets 0/4/8.
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_sink_info_cb(IntPtr c, IntPtr i, int eol, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_context_notify_cb(IntPtr c, IntPtr userdata);

    // -- simple API ----------------------------------------------------

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_new")]
    public static extern IntPtr pa_simple_new(IntPtr server, string name, int dir, string? dev, string stream_name, ref pa_sample_spec ss, IntPtr map, IntPtr attr, out int error);

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_write")]
    public static extern int pa_simple_write(IntPtr p, IntPtr data, UIntPtr bytes, out int error);

    [DllImport("libpulse-simple.so.0", EntryPoint = "pa_simple_free")]
    public static extern void pa_simple_free(IntPtr p);

    // -- async API (used only for sink enumeration) --------------------

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_new")]
    public static extern IntPtr pa_threaded_mainloop_new();

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_free")]
    public static extern void pa_threaded_mainloop_free(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_start")]
    public static extern int pa_threaded_mainloop_start(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_stop")]
    public static extern void pa_threaded_mainloop_stop(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_lock")]
    public static extern void pa_threaded_mainloop_lock(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_unlock")]
    public static extern void pa_threaded_mainloop_unlock(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_wait")]
    public static extern void pa_threaded_mainloop_wait(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_signal")]
    public static extern void pa_threaded_mainloop_signal(IntPtr m, int wait);

    [DllImport("libpulse.so.0", EntryPoint = "pa_threaded_mainloop_get_api")]
    public static extern IntPtr pa_threaded_mainloop_get_api(IntPtr m);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_new")]
    public static extern IntPtr pa_context_new(IntPtr api, string name);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_connect")]
    public static extern int pa_context_connect(IntPtr c, string? server, int flags, IntPtr api);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_disconnect")]
    public static extern void pa_context_disconnect(IntPtr c);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_unref")]
    public static extern void pa_context_unref(IntPtr c);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_set_state_callback")]
    public static extern void pa_context_set_state_callback(IntPtr c, pa_context_notify_cb cb, IntPtr userdata);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_get_state")]
    public static extern int pa_context_get_state(IntPtr c);

    [DllImport("libpulse.so.0", EntryPoint = "pa_context_get_sink_info_list")]
    public static extern IntPtr pa_context_get_sink_info_list(IntPtr c, pa_sink_info_cb cb, IntPtr userdata);

    [DllImport("libpulse.so.0", EntryPoint = "pa_operation_unref")]
    public static extern void pa_operation_unref(IntPtr o);
}
