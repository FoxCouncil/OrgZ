// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class Sidebar : UserControl
{
    internal static readonly DataFormat<string> MediaItemDragFormat = DataFormat.CreateStringApplicationFormat("OrgZ.MediaItem");

    private bool _suppressSelectionChange;

    public Sidebar()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(PlaylistListBox, true);
        PlaylistListBox.AddHandler(DragDrop.DragOverEvent, PlaylistListBox_DragOver);
        PlaylistListBox.AddHandler(DragDrop.DropEvent, PlaylistListBox_Drop);
        PlaylistListBox.ContextRequested += PlaylistListBox_ContextRequested;
        DeviceListBox.ContextRequested += DeviceListBox_ContextRequested;
    }

    private void DeviceListBox_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        var listBoxItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not SidebarItem sb || !sb.IsEnabled)
        {
            e.Handled = true;
            return;
        }

        var menu = new Avalonia.Controls.ContextMenu();

        var rip = new Avalonia.Controls.MenuItem { Header = "Rip CD...", IsEnabled = false };
        menu.Items.Add(rip);

        var eject = new Avalonia.Controls.MenuItem { Header = "Eject" };
        eject.Click += (_, _) =>
        {
            // Future: eject disc via platform API
        };
        eject.IsEnabled = false;
        menu.Items.Add(eject);

        listBoxItem.ContextMenu = menu;
    }

    private void PlaylistListBox_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        var listBoxItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not SidebarItem sb || sb.IsFavorites || sb.IsNewPlaylistAction || sb.IsImportPlaylistAction || !sb.PlaylistId.HasValue)
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var menu = new Avalonia.Controls.ContextMenu();

        var rename = new Avalonia.Controls.MenuItem { Header = "Rename" };
        rename.Click += (_, _) => _ = vm.RenamePlaylist(sb);
        menu.Items.Add(rename);

        var delete = new Avalonia.Controls.MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _ = vm.DeletePlaylist(sb);
        menu.Items.Add(delete);

        menu.Items.Add(new Avalonia.Controls.Separator());

        var exportAs = new Avalonia.Controls.MenuItem { Header = "Export As" };

        var m3u = new Avalonia.Controls.MenuItem { Header = "M3U8" };
        m3u.Click += (_, _) => _ = vm.ExportPlaylist(sb, "M3U8");
        exportAs.Items.Add(m3u);

        var pls = new Avalonia.Controls.MenuItem { Header = "PLS" };
        pls.Click += (_, _) => _ = vm.ExportPlaylist(sb, "PLS");
        exportAs.Items.Add(pls);

        var xspf = new Avalonia.Controls.MenuItem { Header = "XSPF" };
        xspf.Click += (_, _) => _ = vm.ExportPlaylist(sb, "XSPF");
        exportAs.Items.Add(xspf);

        menu.Items.Add(exportAs);

        listBoxItem.ContextMenu = menu;
    }

    private void PlaylistListBox_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(MediaItemDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

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
        if (!e.DataTransfer.Contains(MediaItemDragFormat))
        {
            return;
        }

        var media = MainWindow.DraggedMediaItem;
        if (media == null)
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
