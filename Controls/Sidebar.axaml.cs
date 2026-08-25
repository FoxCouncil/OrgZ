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

        ShareTreeView.ContextRequested += ShareTreeView_ContextRequested;

        // Dragging share rows onto the library's Music node copies them in.
        DragDrop.SetAllowDrop(LibraryListBox, true);
        LibraryListBox.AddHandler(DragDrop.DragOverEvent, LibraryListBox_DragOver);
        LibraryListBox.AddHandler(DragDrop.DropEvent, LibraryListBox_Drop);
    }

    // -- Drag share tracks onto the library's Music node (copy into the library) --

    /// <summary>The live drag payload: the captured multi-selection, else the anchor row.</summary>
    private static List<MediaItem> DraggedMedia()
        => MainWindow.DraggedMediaItems.Count > 0
            ? MainWindow.DraggedMediaItems
            : MainWindow.DraggedMediaItem is { } single ? [single] : [];

    private void LibraryListBox_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        // Only share rows can be copied IN, and only the Music node accepts them -
        // local tracks dropped on the library would be a no-op wearing a copy cursor.
        var media = DraggedMedia();
        if (e.DataTransfer.Contains(MediaItemDragFormat)
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is SidebarItem { ViewConfigKey: "Music" }
            && media.Count > 0
            && media.All(Services.Sharing.ShareDiscovery.IsShareItem))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private async void LibraryListBox_Drop(object? sender, DragEventArgs e)
    {
        var media = DraggedMedia();
        if (e.DataTransfer.Contains(MediaItemDragFormat)
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is SidebarItem { ViewConfigKey: "Music" }
            && DataContext is MainWindowViewModel vm
            && media.Count > 0
            && media.All(Services.Sharing.ShareDiscovery.IsShareItem))
        {
            e.Handled = true;
            await vm.ImportShareTracksToLibraryAsync(media);
        }
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

        // A multi-selection drag imports every dragged track; the anchor-only
        // fallback covers a plain single-row drag.
        List<MediaItem> media = MainWindow.DraggedMediaItems.Count > 0
            ? MainWindow.DraggedMediaItems
            : MainWindow.DraggedMediaItem is { } single ? [single] : [];
        if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext is SidebarItem sb
            && DataContext is MainWindowViewModel vm
            && media.Count > 0)
        {
            e.Handled = true;
            foreach (var track in media)
            {
                await vm.ImportMediaToDeviceAsync(sb, track);
            }
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

            var renameDevice = new Avalonia.Controls.MenuItem { Header = "Rename…" };
            renameDevice.Click += async (_, _) => await vm.RenameDeviceAsync(sb);
            settings.Items.Add(renameDevice);

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
        if (listBoxItem?.DataContext is not SidebarItem sb || sb.IsFavorites || sb.IsNewPlaylistAction || !sb.PlaylistId.HasValue)
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

        // "Sync" submenu - one item per connected device that can take the playlist's TRACKS
        // (the native playlist itself is optional garnish: a Shuffle still gets the songs).
        var sendTo = new Avalonia.Controls.MenuItem { Header = "Sync" };
        var writableDevices = vm.ConnectedDevicesSnapshot().Where(d => IPodDevice.For(d).SupportsTrackAdd).ToList();
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

        // Favorites accepts drops too. It has no PlaylistId - it is a per-track flag - so gating
        // on PlaylistId alone made the star the one playlist row nothing could be dropped on.
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is SidebarItem sb && (sb.PlaylistId.HasValue || sb.IsFavorites))
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

        List<MediaItem> media = MainWindow.DraggedMediaItems.Count > 0
            ? MainWindow.DraggedMediaItems
            : MainWindow.DraggedMediaItem is { } single ? [single] : [];
        if (media.Count == 0)
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not SidebarItem sb || !(sb.PlaylistId.HasValue || sb.IsFavorites))
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            if (sb.IsFavorites)
            {
                vm.FavoriteTracks(media);
            }
            else
            {
                vm.AddTracksToPlaylist(sb.PlaylistId!.Value, media);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// The sidebar's four lists are one logical selection: whichever was clicked keeps it and
    /// the other three clear. Each handler used to spell that out itself - four copies of the
    /// suppress-flag dance, where forgetting one list leaves two rows looking selected.
    /// </summary>
    private void SelectOnly(object? owner, SidebarItem item)
    {
        _suppressSelectionChange = true;
        if (!ReferenceEquals(owner, LibraryListBox)) { LibraryListBox.SelectedItem = null; }
        if (!ReferenceEquals(owner, DeviceTreeView)) { DeviceTreeView.SelectedItem = null; }
        if (!ReferenceEquals(owner, PlaylistListBox)) { PlaylistListBox.SelectedItem = null; }
        if (!ReferenceEquals(owner, ShareTreeView)) { ShareTreeView.SelectedItem = null; }
        _suppressSelectionChange = false;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedSidebarItem = item;
        }
    }

    private void LibraryListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (LibraryListBox.SelectedItem is SidebarItem item)
        {
            SelectOnly(LibraryListBox, item);
        }
    }

    /// <summary>A shared library (or one of its playlists) was picked: it owns the selection.</summary>
    private void ShareTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (ShareTreeView.SelectedItem is SidebarItem item)
        {
            SelectOnly(ShareTreeView, item);
        }
    }

    /// <summary>Right-click on a share's remote playlist offers Import; the share row itself has no verbs.</summary>
    private void ShareTreeView_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        var treeItem = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
        if (treeItem?.DataContext is not SidebarItem sb
            || sb.ViewConfigKey?.StartsWith("SharePlaylist:", StringComparison.Ordinal) != true
            || DataContext is not MainWindowViewModel vm)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        var import = new MenuItem { Header = "Import Playlist" };
        import.Click += async (_, _) => await vm.ImportSharePlaylistAsync(sb);
        menu.Items.Add(import);

        menu.Open(treeItem);
        e.Handled = true;
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

        SelectOnly(DeviceTreeView, item);
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

            SelectOnly(PlaylistListBox, item);
        }
    }
}
