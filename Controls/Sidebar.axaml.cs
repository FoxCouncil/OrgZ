// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class Sidebar : UserControl
{
    private const string MediaItemDragFormat = "OrgZ.MediaItem";

    private bool _suppressSelectionChange;

    public Sidebar()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(PlaylistListBox, true);
        PlaylistListBox.AddHandler(DragDrop.DragOverEvent, PlaylistListBox_DragOver);
        PlaylistListBox.AddHandler(DragDrop.DropEvent, PlaylistListBox_Drop);
    }

    private void PlaylistListBox_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(MediaItemDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        // Only allow drop if hovering over an actual playlist (not Favorites action or "New Playlist...")
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is SidebarItem sb && sb.PlaylistId.HasValue)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void PlaylistListBox_Drop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(MediaItemDragFormat))
        {
            return;
        }

        if (e.Data.Get(MediaItemDragFormat) is not MediaItem media)
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not SidebarItem sb || !sb.PlaylistId.HasValue)
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.AddTrackToPlaylist(sb.PlaylistId.Value, media);
        }

        e.Handled = true;
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
            DeviceListBox.SelectedItem = null;
            PlaylistListBox.SelectedItem = null;
            _suppressSelectionChange = false;
        }
    }

    private void DeviceListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (DeviceListBox.SelectedItem is SidebarItem item)
        {
            _suppressSelectionChange = true;
            LibraryListBox.SelectedItem = null;
            PlaylistListBox.SelectedItem = null;
            _suppressSelectionChange = false;

            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSidebarItem = item;
            }
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
            if (item.IsNewPlaylistAction)
            {
                // Reset selection so the action item doesn't stay selected
                PlaylistListBox.SelectedItem = null;

                if (DataContext is MainWindowViewModel vm2)
                {
                    _ = vm2.CreatePlaylist();
                }

                return;
            }

            _suppressSelectionChange = true;
            LibraryListBox.SelectedItem = null;
            DeviceListBox.SelectedItem = null;
            _suppressSelectionChange = false;

            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSidebarItem = item;
            }
        }
    }
}
