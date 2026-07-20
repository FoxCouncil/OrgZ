// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Views;

/// <summary>
/// Picks one target from a list of device labels. Exists because guessing is a dangerous game:
/// with two iPods plugged in, a bare "Sync" button must ask, not take the first dictionary entry.
/// Returns the selected index via ShowDialog&lt;int?&gt;, null on cancel.
/// </summary>
public partial class DevicePickerDialog : Window
{
    public DevicePickerDialog() : this([]) { }

    public DevicePickerDialog(IReadOnlyList<string> deviceLabels, string? title = null, string? prompt = null)
    {
        InitializeComponent();
        DeviceList.ItemsSource = deviceLabels;
        if (deviceLabels.Count > 0)
        {
            DeviceList.SelectedIndex = 0;
        }
        if (title != null)
        {
            Title = title;
        }
        if (prompt != null)
        {
            PromptText.Text = prompt;
        }
        Loaded += (_, _) => DeviceList.Focus();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedIndex >= 0)
        {
            Close(DeviceList.SelectedIndex);
        }
    }

    private void DeviceList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => OkButton_Click(sender, e);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
