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

        DragDrop.SetAllowDrop(PlaylistTreeView, true);
        PlaylistTreeView.AddHandler(DragDrop.DragOverEvent, PlaylistTree_DragOver);
        PlaylistTreeView.AddHandler(DragDrop.DropEvent, PlaylistTree_Drop);
        PlaylistTreeView.ContextRequested += PlaylistTree_ContextRequested;

        // Reordering a playlist into a folder starts on the row itself; tunnel handlers see
        // the press before the TreeViewItem swallows it for selection.
        PlaylistTreeView.AddHandler(PointerPressedEvent, PlaylistTree_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlaylistTreeView.AddHandler(PointerMovedEvent, PlaylistTree_PointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlaylistTreeView.AddHandler(PointerReleasedEvent, (_, _) => _playlistDragCandidate = null, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Below the last row is still the playlist area: right-click creates here, and a
        // dragged playlist dropped here lands back at the root.
        DragDrop.SetAllowDrop(PlaylistEmptyArea, true);
        PlaylistEmptyArea.AddHandler(DragDrop.DragOverEvent, PlaylistEmptyArea_DragOver);
        PlaylistEmptyArea.AddHandler(DragDrop.DropEvent, PlaylistEmptyArea_Drop);
        PlaylistEmptyArea.ContextRequested += PlaylistEmptyArea_ContextRequested;
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

    /// <summary>"New Playlist..." / "New Folder..." - the create pair, targeted at
    /// <paramref name="folderPath"/> (null = the root).</summary>
    private void AddCreateMenuItems(Avalonia.Controls.ContextMenu menu, MainWindowViewModel vm, string? folderPath)
    {
        var newPlaylist = new Avalonia.Controls.MenuItem { Header = "New Playlist…" };
        newPlaylist.Click += (_, _) => _ = vm.CreatePlaylistInFolderAsync(folderPath);
        menu.Items.Add(newPlaylist);

        var newFolder = new Avalonia.Controls.MenuItem { Header = "New Folder…" };
        newFolder.Click += (_, _) => _ = vm.CreatePlaylistFolderInAsync(folderPath);
        menu.Items.Add(newFolder);
    }

    private void PlaylistEmptyArea_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var menu = new Avalonia.Controls.ContextMenu();
        AddCreateMenuItems(menu, vm, null);
        menu.Open(PlaylistEmptyArea);
        e.Handled = true;
    }

    private void PlaylistTree_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var treeItem = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();

        // The gap between rows inside the tree counts as empty space too.
        if (treeItem?.DataContext is not SidebarItem sb)
        {
            var background = new Avalonia.Controls.ContextMenu();
            AddCreateMenuItems(background, vm, null);
            background.Open(PlaylistTreeView);
            e.Handled = true;
            return;
        }

        if (sb.IsPlaylistFolder)
        {
            var folderMenu = new Avalonia.Controls.ContextMenu();
            AddCreateMenuItems(folderMenu, vm, sb.FolderPath);
            folderMenu.Items.Add(new Avalonia.Controls.Separator());

            var renameFolder = new Avalonia.Controls.MenuItem { Header = "Rename…" };
            renameFolder.Click += (_, _) => _ = vm.RenamePlaylistFolder(sb);
            folderMenu.Items.Add(renameFolder);

            var deleteFolder = new Avalonia.Controls.MenuItem { Header = "Delete Folder" };
            deleteFolder.Click += (_, _) => vm.DeletePlaylistFolder(sb);
            folderMenu.Items.Add(deleteFolder);

            treeItem.ContextMenu = folderMenu;
            folderMenu.Open(treeItem);
            e.Handled = true;
            return;
        }

        if (sb.IsFavorites || !sb.PlaylistId.HasValue)
        {
            e.Handled = true;
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

        treeItem.ContextMenu = menu;
        menu.Open(treeItem);
    }

    // -- Drag a playlist row into (or out of) a virtual folder --

    internal static readonly DataFormat<string> PlaylistNodeDragFormat = DataFormat.CreateStringApplicationFormat("OrgZ.PlaylistNode");

    /// <summary>The playlist row being dragged; static like the media payload - the DataTransfer
    /// string only proves the kind, the object crosses inside the process.</summary>
    private static SidebarItem? _draggedPlaylistNode;

    private SidebarItem? _playlistDragCandidate;
    private Point _playlistDragOrigin;

    /// <summary>The press that started the candidate drag - DoDragDropAsync wants the press
    /// event, not a move (same shape as the media grid's _gridPressEvent).</summary>
    private PointerPressedEventArgs? _playlistPressEvent;

    private void PlaylistTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _playlistDragCandidate = null;

        if (!e.GetCurrentPoint(PlaylistTreeView).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext as SidebarItem;
        if (item?.PlaylistId is not null)
        {
            _playlistDragCandidate = item;
            _playlistDragOrigin = e.GetPosition(PlaylistTreeView);
            _playlistPressEvent = e;
        }
    }

    private async void PlaylistTree_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_playlistDragCandidate is not { } candidate || _playlistPressEvent is not { } press)
        {
            return;
        }

        if (!e.GetCurrentPoint(PlaylistTreeView).Properties.IsLeftButtonPressed)
        {
            _playlistDragCandidate = null;
            return;
        }

        var current = e.GetPosition(PlaylistTreeView);
        var dx = current.X - _playlistDragOrigin.X;
        var dy = current.Y - _playlistDragOrigin.Y;
        if ((dx * dx + dy * dy) < 36)
        {
            return;
        }

        _playlistDragCandidate = null;
        _draggedPlaylistNode = candidate;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(PlaylistNodeDragFormat, "playlist"));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedPlaylistNode = null;
        }
    }

    private void PlaylistTree_DragOver(object? sender, DragEventArgs e)
    {
        var target = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext as SidebarItem;

        if (e.DataTransfer.Contains(PlaylistNodeDragFormat) && _draggedPlaylistNode is { } dragged)
        {
            // A folder takes the playlist; anywhere else in the tree is "back to the root".
            var folder = target is { IsPlaylistFolder: true } ? target.FolderPath : string.Empty;
            e.DragEffects = string.Equals(folder, dragged.FolderPath, StringComparison.OrdinalIgnoreCase)
                ? DragDropEffects.None
                : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (!e.DataTransfer.Contains(MediaItemDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        // Favorites accepts track drops too. It has no PlaylistId - it is a per-track flag - so
        // gating on PlaylistId alone made the star the one playlist row nothing could be dropped on.
        e.DragEffects = target is { } sb && (sb.PlaylistId.HasValue || sb.IsFavorites)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void PlaylistTree_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var target = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext as SidebarItem;

        if (e.DataTransfer.Contains(PlaylistNodeDragFormat) && _draggedPlaylistNode is { } dragged)
        {
            vm.MovePlaylistToFolder(dragged, target is { IsPlaylistFolder: true } ? target.FolderPath : null);
            e.Handled = true;
            return;
        }

        if (!e.DataTransfer.Contains(MediaItemDragFormat))
        {
            return;
        }

        List<MediaItem> media = MainWindow.DraggedMediaItems.Count > 0
            ? MainWindow.DraggedMediaItems
            : MainWindow.DraggedMediaItem is { } single ? [single] : [];
        if (media.Count == 0 || target is null || !(target.PlaylistId.HasValue || target.IsFavorites))
        {
            return;
        }

        if (target.IsFavorites)
        {
            vm.FavoriteTracks(media);
        }
        else
        {
            vm.AddTracksToPlaylist(target.PlaylistId!.Value, media);
        }

        e.Handled = true;
    }

    private void PlaylistEmptyArea_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(PlaylistNodeDragFormat)
            && _draggedPlaylistNode is { } dragged
            && dragged.FolderPath.Length > 0
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PlaylistEmptyArea_Drop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(PlaylistNodeDragFormat)
            && _draggedPlaylistNode is { } dragged
            && DataContext is MainWindowViewModel vm)
        {
            vm.MovePlaylistToFolder(dragged, null);
            e.Handled = true;
        }
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
        if (!ReferenceEquals(owner, PlaylistTreeView)) { PlaylistTreeView.SelectedItem = null; }
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

    private void PlaylistTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange)
        {
            return;
        }

        if (PlaylistTreeView.SelectedItem is not SidebarItem item)
        {
            return;
        }

        // A folder organizes; it isn't a view. Clicking it toggles it open/closed (the
        // chevron chrome is hidden - see the theme) and the stray highlight is cleared so
        // the current view keeps its selection.
        if (item.IsPlaylistFolder)
        {
            if (PlaylistTreeView.TreeContainerFromItem(item) is TreeViewItem container)
            {
                container.IsExpanded = !container.IsExpanded;
            }

            _suppressSelectionChange = true;
            PlaylistTreeView.SelectedItem = null;
            _suppressSelectionChange = false;
            return;
        }

        SelectOnly(PlaylistTreeView, item);
    }
}
