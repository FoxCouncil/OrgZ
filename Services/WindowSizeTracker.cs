// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OrgZ.Services;

/// <summary>
/// Remembers per-window resize state across sessions.  Each window that calls
/// <see cref="Track"/> gets the saved width/height applied on open, and any
/// subsequent resize is debounced and persisted back into <see cref="Settings"/>
/// under a single <c>OrgZ.WindowSizes</c> dictionary key.  No position tracking —
/// the window manager handles placement, users just want sizes to stick.
/// </summary>
public static class WindowSizeTracker
{
    internal const string SettingsKey = "OrgZ.WindowSizes";
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(400);

    private static Dictionary<string, SavedSize> _cache = Load();
    private static readonly Lock _cacheLock = new();

    /// <summary>
    /// Applies any previously-saved size for <paramref name="windowKey"/> to
    /// <paramref name="window"/>, and starts persisting further size changes.
    /// Safe to call from a Window constructor after <c>InitializeComponent</c>.
    /// </summary>
    public static void Track(Window window, string windowKey)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrEmpty(windowKey);

        SavedSize? saved;
        lock (_cacheLock)
        {
            saved = _cache.TryGetValue(windowKey, out var s) ? s : null;
        }

        if (saved.HasValue)
        {
            // Overrule SizeToContent — the user's previous manual size wins.
            window.SizeToContent = SizeToContent.Manual;
            window.Width = ClampToMin(saved.Value.Width, window.MinWidth);
            window.Height = ClampToMin(saved.Value.Height, window.MinHeight);
        }

        DispatcherTimer? debounce = null;
        bool loaded = false;

        window.Opened += (_, _) => loaded = true;

        window.Resized += (_, e) =>
        {
            if (!loaded || window.WindowState != WindowState.Normal)
            {
                return;
            }

            var w = e.ClientSize.Width;
            var h = e.ClientSize.Height;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0)
            {
                return;
            }

            debounce?.Stop();
            debounce = new DispatcherTimer(DebounceInterval, DispatcherPriority.Background, (_, _) =>
            {
                debounce?.Stop();
                debounce = null;
                // Window.Width/Height reflect the outer frame size — restore this exactly
                // on the next open, so chrome height/border changes between sessions don't
                // accumulate drift.
                Save(windowKey, window.Width, window.Height);
            });
            debounce.Start();
        };
    }

    /// <summary>
    /// Clears all persisted window sizes.  Next time each window opens, it
    /// falls back to its XAML-declared default size or <see cref="SizeToContent"/>
    /// behavior, as if this user had never resized anything.
    /// </summary>
    public static void ResetAll()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }

        Settings.Set(SettingsKey, string.Empty);
        Settings.Save();
    }

    /// <summary>
    /// Test hook: discards the in-memory cache so the next <see cref="Track"/>
    /// re-reads from the current <see cref="Settings"/> state.
    /// </summary>
    internal static void ReloadForTesting()
    {
        lock (_cacheLock)
        {
            _cache = Load();
        }
    }

    /// <summary>
    /// Returns the currently-persisted size for <paramref name="windowKey"/>,
    /// or <see langword="null"/> if the user hasn't resized that window yet.
    /// </summary>
    internal static SavedSize? GetSaved(string windowKey)
    {
        lock (_cacheLock)
        {
            return _cache.TryGetValue(windowKey, out var s) ? s : null;
        }
    }

    internal static void SetSaved(string windowKey, double width, double height)
    {
        Save(windowKey, width, height);
    }

    private static void Save(string windowKey, double width, double height)
    {
        string json;
        lock (_cacheLock)
        {
            _cache[windowKey] = new SavedSize { Width = width, Height = height };
            json = JsonSerializer.Serialize(_cache);
        }

        // Stored as a JSON string because Settings.Get<T> routes non-JsonElement
        // values through Convert.ChangeType, which can't coerce arbitrary
        // dictionaries.  A plain string round-trips cleanly whether the value
        // was just written in this session or reloaded from disk.
        Settings.Set(SettingsKey, json);
        Settings.Save();
    }

    private static Dictionary<string, SavedSize> Load()
    {
        try
        {
            var json = Settings.Get<string>(SettingsKey, "");
            if (string.IsNullOrEmpty(json))
            {
                return [];
            }

            return JsonSerializer.Deserialize<Dictionary<string, SavedSize>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static double ClampToMin(double value, double minimum)
    {
        if (double.IsNaN(minimum) || minimum <= 0)
        {
            return value;
        }

        return value < minimum ? minimum : value;
    }

    public readonly record struct SavedSize
    {
        public double Width { get; init; }
        public double Height { get; init; }
    }
}
