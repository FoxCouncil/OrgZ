// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Controls;

public partial class StatusBar : UserControl
{
    public event Action? ErrorButtonClicked;

    public StatusBar()
    {
        InitializeComponent();
    }

    private void ErrorButton_Click(object? sender, RoutedEventArgs e)
    {
        ErrorButtonClicked?.Invoke();
    }
}
