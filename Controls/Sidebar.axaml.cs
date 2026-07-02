// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrgZ.Services;
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
        DeviceTreeView.ContextRequested += DeviceTreeView_ContextRequested;

        DragDrop.SetAllowDrop(DeviceTreeView, true);
        DeviceTreeView.AddHandler(DragDrop.DragOverEvent, DeviceTreeView_DragOver);
        DeviceTreeView.AddHandler(DragDrop.DropEvent, DeviceTreeView_Drop);
    }

    // -- Drag a library track onto a device node (iPod import; Music only, for now) --

    private void DeviceTreeView_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (e.DataTransfer.Contains(MediaItemDragFormat)
            && (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext is SidebarItem sb
            && DataContext is MainWindowViewModel vm
            && vm.CanAcceptMediaDrop(sb, MainWindow.DraggedMediaItem))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private async void DeviceTreeView_Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(MediaItemDragFormat))
        {
            return;
        }
        var media = MainWindow.DraggedMediaItem;
        if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext is SidebarItem sb
            && DataContext is MainWindowViewModel vm
            && media is not null)
        {
            e.Handled = true;
            await vm.ImportMediaToDeviceAsync(sb, media);
        }
    }

    private void DeviceTreeView_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        // Hit-test for a TreeViewItem ancestor - context menu applies to whichever
        // node the user right-clicked (device parent or one of its children).
        var treeItem = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
        if (treeItem?.DataContext is not SidebarItem sb || !sb.IsEnabled)
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var menu = new Avalonia.Controls.ContextMenu();

        // iPod / Rockbox / GenericPlayer devices use "Device:{mountPath}" as their view key;
        // CD audio uses the fixed "CdAudio" key. Branch the menu accordingly.
        if (sb.ViewConfigKey?.StartsWith("Device:") == true)
        {
            // Top level is the two everyday actions only - Sync and Eject. Everything else
            // (config + the destructive erase) lives under Settings.
            var sync = new Avalonia.Controls.MenuItem { Header = "Sync" };
            sync.Click += async (_, _) => await vm.SyncDeviceAsync(sb);
            menu.Items.Add(sync);

            var eject = new Avalonia.Controls.MenuItem { Header = "Eject" };
            eject.Click += (_, _) => vm.EjectDevice(sb);
            menu.Items.Add(eject);

            menu.Items.Add(new Avalonia.Controls.Separator());

            var settings = new Avalonia.Controls.MenuItem { Header = "Settings" };

            var refresh = new Avalonia.Controls.MenuItem { Header = "Refresh Device Info" };
            refresh.Click += (_, _) => vm.RefreshDeviceInfo(sb);
            settings.Items.Add(refresh);

            var syncSettings = new Avalonia.Controls.MenuItem { Header = "Sync Settings…" };
            syncSettings.Click += async (_, _) => await vm.SyncDeviceAsync(sb, forceSettings: true);
            settings.Items.Add(syncSettings);

            settings.Items.Add(new Avalonia.Controls.Separator());

            var erase = new Avalonia.Controls.MenuItem { Header = "Erase iPod…" };
            erase.Click += async (_, _) => await vm.EraseDeviceAsync(sb);
            settings.Items.Add(erase);

            menu.Items.Add(settings);
        }
        else
        {
            // The CD node: both services exist, so both act (they used to be dead placeholders).
            var rip = new Avalonia.Controls.MenuItem { Header = "Rip CD…" };
            rip.Click += async (_, _) => await vm.RipCurrentCdAsync();
            menu.Items.Add(rip);

            var eject = new Avalonia.Controls.MenuItem { Header = "Eject" };
            eject.Click += (_, _) => vm.EjectCdCommand.Execute(null);
            menu.Items.Add(eject);
        }

        // Open it now: assigning ContextMenu alone needs a SECOND right-click (the first only wires
        // it up). Opening here makes it appear on the first click. (Same pattern as the header menu.)
        treeItem.ContextMenu = menu;
        menu.Open(treeItem);
    }

    private void LibraryListBox_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        // Only the Audiobooks entry carries a menu today - the import gesture for books the user
        // already owns. Everything else swallows the click rather than showing an empty menu.
        var listBoxItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not SidebarItem sb || sb.ViewConfigKey != "Audiobooks" || DataContext is not MainWindowViewModel vm)
        {
            e.Handled = true;
            return;
        }

        var menu = new Avalonia.Controls.ContextMenu();
        var import = new Avalonia.Controls.MenuItem { Header = "Import Audiobooks…" };
        import.Click += (_, _) => _ = vm.ImportAudiobooksAsync();
        menu.Items.Add(import);

        listBoxItem.ContextMenu = menu;
        menu.Open(listBoxItem);
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

        // "Sync" submenu - one item per connected device that can take a playlist (Rockbox, binary
        // iTunesDB iPods, and the Nano 5G via its SQLite + CDB stack). Capability comes from the
        // IPodDevice model, not the blanket "stock = read-only" flag, so genuinely-writable iPods show.
        var sendTo = new Avalonia.Controls.MenuItem { Header = "Sync" };
        var writableDevices = vm.ConnectedDevicesSnapshot().Where(d => IPodDevice.For(d).SupportsPlaylists).ToList();
        if (writableDevices.Count == 0)
        {
            var none = new Avalonia.Controls.MenuItem { Header = "No compatible devices", IsEnabled = false };
            sendTo.Items.Add(none);
        }
        else
        {
            foreach (var device in writableDevices)
            {
                var dev = device;   // capture
                var deviceItem = new Avalonia.Controls.MenuItem { Header = dev.SidebarLabel };
                deviceItem.Click += (_, _) => _ = vm.SendPlaylistToDevice(sb, dev);
                sendTo.Items.Add(deviceItem);
            }
        }
        menu.Items.Add(sendTo);

        listBoxItem.ContextMenu = menu;
        menu.Open(listBoxItem);
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

        if (LibraryListBox.SelectedItem is SidebarItem item)
        {
            _suppressSelectionChange = true;
            DeviceTreeView.SelectedItem = null;
            PlaylistListBox.SelectedItem = null;
            _suppressSelectionChange = false;

            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSidebarItem = item;
            }
        }
    }

    private void DeviceTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (DeviceTreeView.SelectedItem is not SidebarItem item)
        {
            return;
        }

        // The device row itself is the music view (its ViewConfigKey is "Device:{mount}")
        // so clicking it navigates normally. The "Playlists" sub-parent IS a pure
        // container though: clicking it jumps to the first playlist when any exist, so
        // the main grid never shows the empty placeholder unless the device genuinely
        // has no playlists.
        bool isPlaylistsContainer = item.ViewConfigKey?.EndsWith(":Playlists") == true;
        if (isPlaylistsContainer && item.Children.Count > 0)
        {
            var firstChild = item.Children[0];
            _suppressSelectionChange = true;
            // Programmatically move selection to the child inside the tree so the
            // visual highlight lands on the right row.
            var container = DeviceTreeView.TreeContainerFromItem(firstChild);
            if (container is TreeViewItem tvi)
            {
                tvi.IsSelected = true;
            }
            _suppressSelectionChange = false;
            item = firstChild;
        }

        _suppressSelectionChange = true;
        LibraryListBox.SelectedItem = null;
        PlaylistListBox.SelectedItem = null;
        _suppressSelectionChange = false;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedSidebarItem = item;
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
            DeviceTreeView.SelectedItem = null;
            _suppressSelectionChange = false;

            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSidebarItem = item;
            }
        }
    }
}
