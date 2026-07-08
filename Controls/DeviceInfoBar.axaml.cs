// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrgZ.Models;
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
    /// Manual retry for the privileged identity read (serial + OS version) on the Software
    /// Version row. The read now runs automatically on first connect
    /// (<c>MainWindowViewModel.MaybeAutoReadIdentityAsync</c>); this affordance stays as the
    /// path back for a device where the user declined that first authorization. Both call
    /// the same <c>ReadDeviceIdentityAsync</c>.
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

        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        e.Handled = true;
        _firmwareReadInFlight = true;
        try
        {
            await vm.ReadDeviceIdentityAsync(device);
        }
        finally
        {
            _firmwareReadInFlight = false;
        }
    }
}
