// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Shared population logic for the "speaker icon" audio-output flyout used by
/// both <c>MiniPlayerWindow</c> and <c>MainWindow</c>. Keeps the row layout,
/// provider grouping, and selection wiring identical between the two surfaces
/// so users see the same picker regardless of which window they opened it from.
/// </summary>
internal static class AudioOutputFlyoutHelper
{
    public static void Populate(AudioOutputManager manager, StackPanel deviceList)
    {
        deviceList.Children.Clear();

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

            deviceList.Children.Add(BuildRow(manager, device, activeSinks));
        }
    }

    private static Control BuildRow(AudioOutputManager manager, AudioDeviceInfo device, Dictionary<string, IAudioSink> activeSinks)
    {
        var active = activeSinks.TryGetValue(device.QualifiedId, out var sink);
        var initialVolume = sink?.Volume ?? 1f;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 1, 0, 1),
        };

        // Unavailable devices (e.g. AirPlay until streaming lands) render disabled with a suffix -
        // visible so the user knows they exist, unselectable so they can't silently eat the audio.
        var check = new CheckBox { IsChecked = active, VerticalAlignment = VerticalAlignment.Center, IsEnabled = device.IsAvailable };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var label = new TextBlock
        {
            Text = device.IsAvailable ? device.DisplayName : $"{device.DisplayName} — coming soon",
            Opacity = device.IsAvailable ? 1.0 : 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

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

        check.IsCheckedChanged += (_, _) =>
        {
            slider.IsEnabled = check.IsChecked == true;
            ApplySelection(manager, device, check, slider);
        };

        slider.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name == nameof(Slider.Value))
            {
                ApplySelection(manager, device, check, slider);
            }
        };

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
