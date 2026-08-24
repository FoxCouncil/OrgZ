// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Shared population logic for the "speaker icon" audio-output flyout used by
/// both <c>MiniPlayerWindow</c> and <c>MainWindow</c>. Keeps the row layout,
/// provider grouping, and selection wiring identical between the two surfaces
/// so users see the same picker regardless of which window they opened it from.
/// </summary>
internal static class AudioOutputFlyoutHelper
{
    private static readonly Serilog.ILogger _log = Logging.For("AudioOutputFlyout");

    /// <summary>
    /// The Window a modal dialog can be parented to from inside the picker.
    ///
    /// A flyout's contents do not live in the Window: unless overlay popups are enabled, the
    /// visual root of a row is a PopupRoot, which is a WindowBase but NOT a Window. Matching on
    /// Window alone therefore fails in the default configuration on every platform, and the
    /// takeover confirmation silently never appears - the speaker just gets taken. Avalonia
    /// keeps the popup-to-parent link behind an internal interface, so the owner is taken from
    /// the application's own window list instead: the picker is always opened from one of our
    /// two windows, and that window is the active one while its flyout is up.
    /// </summary>
    private static Window? OwnerWindow(Visual from)
    {
        if (TopLevel.GetTopLevel(from) is Window window)
        {
            return window;
        }

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        }

        _log.Debug("No owner window resolved for the audio output picker; skipping the confirmation dialog");
        return null;
    }

    public static void Populate(AudioOutputManager manager, StackPanel deviceList)
    {
        deviceList.Children.Clear();
        _rows.Clear();

        var devices = manager.EnumerateAllDevices();
        var activeSinks = manager.Bus.Sinks.ToDictionary(s => s.Id, s => s);

        string? lastProvider = null;
        foreach (var device in devices)
        {
            if (device.ProviderName != lastProvider)
            {
                lastProvider = device.ProviderName;
                deviceList.Children.Add(new TextBlock
                {
                    Text = device.ProviderName,
                    FontWeight = FontWeight.Bold,
                    FontSize = 11,
                    Opacity = 0.75,
                    Margin = new Thickness(0, 6, 0, 2),
                });
            }

            var row = BuildRow(manager, device, activeSinks, out var visuals);
            deviceList.Children.Add(row);
            _rows[device.QualifiedId] = visuals;
        }

        Subscribe(manager, deviceList);
    }

    /// <summary>
    /// The controls of each built row, kept so a state change can be painted onto the existing
    /// rows instead of rebuilding them. Keyed by <see cref="AudioDeviceInfo.QualifiedId"/>.
    /// </summary>
    private static readonly Dictionary<string, RowVisuals> _rows = [];

    private sealed record RowVisuals(CheckBox Check, Slider Slider, TextBlock Label, StackPanel LabelCell);

    /// <summary>
    /// The single live DevicesChanged subscription, replaced rather than added to. One handler
    /// per Populate would accumulate - N opens leaves N handlers, and the next change runs
    /// Populate N times back to back.
    /// </summary>
    private static EventHandler? _onDevicesChanged;

    private static void Subscribe(AudioOutputManager manager, StackPanel deviceList)
    {
        if (_onDevicesChanged is not null)
        {
            manager.DevicesChanged -= _onDevicesChanged;
        }

        _onDevicesChanged = (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!deviceList.IsAttachedToVisualTree())
                {
                    return;
                }

                Refresh(manager, deviceList);
            });
        };

        manager.DevicesChanged += _onDevicesChanged;
    }

    /// <summary>
    /// Paints a device-state change onto the rows already on screen, falling back to a full
    /// rebuild only when the set of devices itself changed.
    ///
    /// Ticking a HomePod makes it re-announce its status flags within a second or two, and the
    /// provider raises DevicesChanged on any flag change - so a full rebuild here would tear
    /// down the very row the user is still touching, dropping the slider out from under a drag
    /// in progress. Everything that actually changes (the play/pause glyph, reachability) is a
    /// property of controls that can stay exactly where they are.
    /// </summary>
    private static void Refresh(AudioOutputManager manager, StackPanel deviceList)
    {
        var devices = manager.EnumerateAllDevices();

        if (devices.Count != _rows.Count || devices.Any(d => !_rows.ContainsKey(d.QualifiedId)))
        {
            Populate(manager, deviceList);
            return;
        }

        foreach (var device in devices)
        {
            if (!_rows.TryGetValue(device.QualifiedId, out var visuals))
            {
                continue;
            }

            visuals.Check.IsEnabled = device.IsAvailable;
            visuals.Label.Text = device.IsAvailable ? device.DisplayName : $"{device.DisplayName} — unreachable";
            visuals.Label.Opacity = device.IsAvailable ? 1.0 : 0.5;
            SetStateIcon(visuals.LabelCell, device);
        }
    }


    /// <summary>
    /// Set while a slider is being moved to FOLLOW a device rather than to drive one, so the
    /// change handler can tell the two apart. The flyout is one control on one thread, so a
    /// single flag covers every row.
    /// </summary>
    private static bool _suppressPush;

    /// <summary>Guards the programmatic un-check after a declined takeover from re-entering the handler.</summary>
    private static bool _revertingCheck;

    /// <summary>
    /// Puts the live-state glyph on a row, or takes it off. Read off the receiver's own
    /// broadcast - no connection was opened to know this. Playing beats in-use when both bits
    /// are set: "someone hears audio from this right now" is the fact that matters before
    /// taking it over.
    /// </summary>
    private static void SetStateIcon(StackPanel labelCell, AudioDeviceInfo device)
    {
        // The label is child 0; anything after it is the glyph from a previous paint.
        while (labelCell.Children.Count > 1)
        {
            labelCell.Children.RemoveAt(labelCell.Children.Count - 1);
        }

        if (!device.IsPlayingAudio && !device.IsReceivingAirPlay)
        {
            return;
        }

        labelCell.Children.Add(new Optris.Icons.Avalonia.Icon
        {
            Value = device.IsPlayingAudio ? "fa-solid fa-play" : "fa-solid fa-pause",
            FontSize = 9,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = device.IsPlayingAudio ? "Playing" : "In use",
        });
    }

    private static Control BuildRow(AudioOutputManager manager, AudioDeviceInfo device, Dictionary<string, IAudioSink> activeSinks, out RowVisuals visuals)
    {
        var active = activeSinks.TryGetValue(device.QualifiedId, out var sink);
        var initialVolume = sink?.Volume ?? 1f;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 1, 0, 1),
        };

        // Unavailable devices (an AirPlay receiver seen by name but not yet resolved to an
        // address) render disabled - visible so the user knows they exist, unselectable so
        // they can't silently eat the audio.
        var check = new CheckBox { IsChecked = active, VerticalAlignment = VerticalAlignment.Center, IsEnabled = device.IsAvailable };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var label = new TextBlock
        {
            Text = device.IsAvailable ? device.DisplayName : $"{device.DisplayName} — unreachable",
            Opacity = device.IsAvailable ? 1.0 : 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var labelCell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        labelCell.Children.Add(label);
        SetStateIcon(labelCell, device);

        Grid.SetColumn(labelCell, 1);
        grid.Children.Add(labelCell);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = initialVolume * 100,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 100,
            IsEnabled = active,
        };
        Grid.SetColumn(slider, 2);
        grid.Children.Add(slider);

        check.IsCheckedChanged += async (_, _) =>
        {
            if (_revertingCheck)
            {
                return;
            }

            // An async void handler: an exception escaping it has nowhere to go but the
            // process. The dialog can throw for ordinary reasons - the owner closing while the
            // picker is up, or already showing a modal - and none of them are worth a crash.
            try
            {
                // Busy-ness is read from the receiver's own broadcast, and the row's copy was
                // captured when it was built. Re-read it at click time: the flags change while
                // the picker sits open, and a stale "idle" is a takeover with no question asked.
                var current = manager.EnumerateAllDevices().FirstOrDefault(d => d.QualifiedId == device.QualifiedId) ?? device;

                // Taking over a busy speaker is SILENT preemption - an AirPlay 2 receiver
                // simply hands itself to the new session, mid-song, no questions asked. The
                // question gets asked here instead. Busy-ness is read passively off the
                // receiver's own broadcast, so an idle speaker pays no dialog and no traffic.
                if (check.IsChecked == true
                    && current.IsBusy
                    && manager.Bus.Sinks.All(s => s.Id != device.QualifiedId)
                    && OwnerWindow(check) is { } owner)
                {
                    var confirmed = await new Views.ConfirmDialog(
                        "Speaker In Use",
                        $"{device.DisplayName} is playing something else.",
                        "Take Over").ShowDialog<bool?>(owner);

                    if (confirmed != true)
                    {
                        _revertingCheck = true;
                        try
                        {
                            check.IsChecked = false;
                        }
                        finally
                        {
                            _revertingCheck = false;
                        }

                        slider.IsEnabled = false;
                        return;
                    }
                }

                slider.IsEnabled = check.IsChecked == true;
                ApplySelection(manager, device, check, slider);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Audio output selection failed for {Device}", device.DisplayName);
            }
        };

        // A speaker's volume can move without us: someone touches the HomePod, or drags the
        // slider in the Home app. The row follows it while it is on screen, so the picker
        // shows where the speaker actually IS rather than where this app last put it.
        //
        // Guarded by _suppressPush, because moving the slider from here would otherwise look
        // exactly like a drag and send the level straight back to the device that just set
        // it - and the two would chase each other for as long as the user kept dragging.
        void OnRemoteVolume(object? _, (string SinkId, float Level) change)
        {
            if (!change.SinkId.Equals(device.QualifiedId, StringComparison.Ordinal))
            {
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _suppressPush = true;
                try
                {
                    slider.Value = Math.Clamp(change.Level, 0f, 1f) * 100;
                }
                finally
                {
                    _suppressPush = false;
                }
            });
        }

        manager.Bus.RemoteVolume += OnRemoteVolume;
        grid.DetachedFromVisualTree += (_, _) => manager.Bus.RemoteVolume -= OnRemoteVolume;

        // An UNSELECTED receiver is not asked what volume it is set to. Reading it means an
        // RTSP connection per speaker, and a second connection to a HomePod leaves the
        // follow-up session hanging - metadata and controls died the last time this list did
        // that. A selected speaker reports its own level over the event channel instead, and
        // the connect-time adoption puts the true level on the slider the moment it is ticked.

        slider.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name != nameof(Slider.Value) || _suppressPush)
            {
                return;
            }

            // A drag fires this per tick. Set the live sink's gain directly and let the
            // persist trail behind (deferred) - the old path rebuilt the entire selection
            // set, re-enumerated providers, and rewrote settings.json once per pixel.
            if (manager.Bus.Sinks.FirstOrDefault(s => s.Id == device.QualifiedId) is { } live)
            {
                live.Volume = (float)(slider.Value / 100.0);
                manager.SavePersistedSelections(deferred: true);
            }
            else
            {
                ApplySelection(manager, device, check, slider);
            }
        };

        visuals = new RowVisuals(check, slider, label, labelCell);
        return grid;
    }

    private static void ApplySelection(AudioOutputManager manager, AudioDeviceInfo device, CheckBox check, Slider slider)
    {
        // Rebuild from the currently-active sinks so toggling one device
        // doesn't drop the others. The bus owns the full selection state.
        var selections = manager.Bus.Sinks
            .Where(s => s.Id != device.QualifiedId)
            .Select(s => new AudioOutputManager.SinkSelection
            {
                QualifiedId = s.Id,
                Volume = s.Volume,
                IsMuted = s.IsMuted,
            })
            .ToList();

        if (check.IsChecked == true)
        {
            selections.Add(new AudioOutputManager.SinkSelection
            {
                QualifiedId = device.QualifiedId,
                Volume = (float)(slider.Value / 100.0),
                IsMuted = false,
            });
        }

        manager.ApplySelections(selections);
        manager.SavePersistedSelections();
    }
}
