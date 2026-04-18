// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using OrgZ.Models;

namespace OrgZ.Controls;

public partial class DeviceInfoBar : UserControl
{
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
}
