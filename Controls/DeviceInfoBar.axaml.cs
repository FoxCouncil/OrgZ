// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.Services;
using OrgZ.ViewModels;
using Serilog;

namespace OrgZ.Controls;

public partial class DeviceInfoBar : UserControl
{
    private static readonly ILogger _log = Logging.For<DeviceInfoBar>();
    private bool _firmwareReadInFlight;

    public DeviceInfoBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the Sync menu for the iPod - the device-level entry point for pushing content. Music
    /// and playlists sync from their own views; this menu covers the device-scoped batch syncs
    /// (podcasts today, extensible). Built in code-behind so the menu commands resolve against the
    /// window's view model without popup binding gymnastics.
    /// </summary>
    private void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel vm || sender is not Control target)
        {
            return;
        }

        var podcasts = new MenuItem { Header = "Podcasts" };
        podcasts.Click += (_, _) => vm.SyncPodcastsToIPodCommand.Execute(null);

        var flyout = new MenuFlyout();
        flyout.Items.Add(podcasts);
        flyout.ShowAt(target);
    }

    /// <summary>
    /// Toggles the Model row between the libgpod-decoded iPod identity and the raw
    /// hardware model string from WMI (surfaces drive-adapter mods like "iFlash").
    /// Does nothing when we don't have a hardware string to switch to.
    /// </summary>
    private void ModelValueText_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ConnectedDevice device && device.HasHardwareModel)
        {
            device.ShowHardwareModel = !device.ShowHardwareModel;
            e.Handled = true;
        }
    }

    /// <summary>
    /// "Read iPod OS version..." affordance on the Software Version row. Only
    /// active when the device is stock iPod and AppleFirmwareVersion is empty
    /// (see <see cref="ConnectedDevice.IsAppleFirmwareReadable"/>). Spawns the
    /// elevated CD helper to read the firmware partition; on success, updates
    /// the live device and writes the result to <c>/.orgz/device</c> so future
    /// non-admin scans pick it up via the merge-on-read path.
    /// </summary>
    private async void FirmwareValue_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ConnectedDevice device || !device.IsAppleFirmwareReadable || _firmwareReadInFlight)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _firmwareReadInFlight = true;
        try
        {
            var result = await IPodFirmwareElevation.ReadAsync(device.MountPath, device.IpodGeneration);

            if (!string.IsNullOrWhiteSpace(result.Version))
            {
                device.AppleFirmwareVersion = result.Version;
                DeviceFingerprint.PersistDeviceRecord(device);
                _log.Information("Persisted Apple firmware version {Version} for device at {MountPath}", result.Version, device.MountPath);
            }
            else if (result.UserDeclined)
            {
                _log.Information("User declined UAC for iPod firmware read on {MountPath}", device.MountPath);
            }
            else
            {
                _log.Warning("iPod firmware read returned no version for {MountPath}: {Diagnostic}", device.MountPath, result.Diagnostic);
            }
        }
        finally
        {
            _firmwareReadInFlight = false;
        }
    }
}
