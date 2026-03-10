// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class Sidebar : UserControl
{
    private bool _suppressSelectionChange;

    public Sidebar()
    {
        InitializeComponent();
    }

    private void LibraryListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (LibraryListBox.SelectedItem != null)
        {
            _suppressSelectionChange = true;
            PlaylistListBox.SelectedItem = null;
            _suppressSelectionChange = false;
        }
    }

    private void PlaylistListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (PlaylistListBox.SelectedItem is SidebarItem item)
        {
            _suppressSelectionChange = true;
            LibraryListBox.SelectedItem = null;
            _suppressSelectionChange = false;

            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSidebarItem = item;
            }
        }
    }
}
