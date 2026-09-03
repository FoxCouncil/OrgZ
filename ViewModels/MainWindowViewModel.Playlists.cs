// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LibVLCSharp.Shared;
using OrgZ.Services.Audiobooks;
using OrgZ.Services.DeviceHelper;
using System.Net.Http;
using Serilog;

namespace OrgZ.ViewModels;

/// <summary>Playlists: CRUD, ordering, import/export, and the playlist header.</summary>
internal partial class MainWindowViewModel
{

    /// <summary>
    /// Rebuilds the sidebar's playlists from the database. The docs screenshot harness writes
    /// playlists to its own scratch database and calls this, so the shots go through the same
    /// path the app does rather than a parallel fake.
    /// </summary>
    internal void ReloadPlaylistsForScreenshots() => LoadPlaylistSidebarItems();

    /// <summary>Folders the user created that are still empty; folders with playlists in them
    /// re-derive from the files' #ORGZ-FOLDER directives on every load.</summary>
    private const string PlaylistFoldersKey = "OrgZ.Playlists.Folders";

    private void LoadPlaylistSidebarItems()
    {
        // Rebuild after Favorites (index 0): the folders are derived state, so patching the
        // tree in place would mean diffing it. Selection is keyed and restored by the caller.
        while (PlaylistItems.Count > 1)
        {
            PlaylistItems.RemoveAt(PlaylistItems.Count - 1);
        }

        var playlists = MediaCache.LoadAllPlaylists();

        // Every folder anything references, saved empties included. SortedSet gives "A" before
        // "A/B" before "B", which is exactly creation order for nested nodes.
        var folderPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in Settings.Get<List<string>>(PlaylistFoldersKey, []) ?? [])
        {
            var normalized = PlaylistFolderSync.NormalizeFolder(saved);
            if (normalized.Length > 0)
            {
                folderPaths.Add(normalized);
            }
        }
        foreach (var playlist in playlists)
        {
            var normalized = PlaylistFolderSync.NormalizeFolder(playlist.Folder);
            if (normalized.Length > 0)
            {
                folderPaths.Add(normalized);
            }
        }

        var nodesByPath = new Dictionary<string, SidebarItem>(StringComparer.OrdinalIgnoreCase);

        SidebarItem NodeFor(string path)
        {
            if (nodesByPath.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var slash = path.LastIndexOf('/');
            var node = new SidebarItem
            {
                Name = slash < 0 ? path : path[(slash + 1)..],
                Icon = "fa-solid fa-folder",
                Category = "PLAYLISTS",
                IsEnabled = true,
                IsPlaylistFolder = true,
                FolderPath = path,
                ViewConfigKey = $"PlaylistFolder:{path}",
            };
            nodesByPath[path] = node;

            if (slash < 0)
            {
                PlaylistItems.Add(node);
            }
            else
            {
                NodeFor(path[..slash]).Children.Add(node);
            }

            return node;
        }

        // Folders first at every level (they're added before any playlist), playlists after
        // in the name order LoadAllPlaylists already gives.
        foreach (var path in folderPaths)
        {
            NodeFor(path);
        }

        foreach (var playlist in playlists)
        {
            var key = $"Playlist:{playlist.Id}";
            var trackIds = MediaCache.GetPlaylistTrackIds(playlist.Id);
            ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlist.Id, trackIds));

            var folder = PlaylistFolderSync.NormalizeFolder(playlist.Folder);
            var row = new SidebarItem
            {
                Name = playlist.Name,
                Icon = "fa-solid fa-list-ul",
                Category = "PLAYLISTS",
                IsEnabled = true,
                ViewConfigKey = key,
                PlaylistId = playlist.Id,
                FolderPath = folder,
            };

            if (folder.Length == 0)
            {
                PlaylistItems.Add(row);
            }
            else
            {
                NodeFor(folder).Children.Add(row);
            }
        }
    }

    /// <summary>Depth-first walk of the playlist tree: Favorites, folders, and playlist rows.</summary>
    internal IEnumerable<SidebarItem> FlattenedPlaylistItems()
    {
        static IEnumerable<SidebarItem> Walk(IEnumerable<SidebarItem> items)
        {
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in Walk(item.Children))
                {
                    yield return child;
                }
            }
        }

        return Walk(PlaylistItems);
    }

    internal IReadOnlyList<string> PlaylistFolderPaths() =>
        FlattenedPlaylistItems().Where(i => i.IsPlaylistFolder).Select(i => i.FolderPath).ToList();

    private static List<string> SavedPlaylistFolders() =>
        Settings.Get<List<string>>(PlaylistFoldersKey, []) ?? [];

    private static void SavePlaylistFolders(IEnumerable<string> folders)
    {
        Settings.Set(PlaylistFoldersKey, folders
            .Select(PlaylistFolderSync.NormalizeFolder)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList());
        Settings.SaveDeferred();
    }

    [RelayCommand]
    internal Task CreatePlaylist() => CreatePlaylistInFolderAsync(null);

    internal async Task CreatePlaylistInFolderAsync(string? folder)
    {
        var dialog = new Views.PlaylistNameDialog();
        var result = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var id = MediaCache.CreatePlaylist(result.Trim(), folder: PlaylistFolderSync.NormalizeFolder(folder));
        ExportPlaylistFile(id);
        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();

        // Navigate to the new playlist
        var newItem = FlattenedPlaylistItems().FirstOrDefault(i => i.PlaylistId == id);
        if (newItem != null)
        {
            SelectedSidebarItem = newItem;
        }
    }

    [RelayCommand]
    internal Task CreatePlaylistFolder() => CreatePlaylistFolderInAsync(null);

    internal async Task CreatePlaylistFolderInAsync(string? parent)
    {
        var dialog = new Views.PlaylistNameDialog(null, title: "New Folder", prompt: "Folder name:");
        var result = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var name = PlaylistFolderSync.NormalizeFolder(result);
        if (name.Length == 0)
        {
            return;
        }

        var path = PlaylistFolderSync.NormalizeFolder(
            string.IsNullOrEmpty(parent) ? name : parent + "/" + name);

        var folders = SavedPlaylistFolders();
        folders.Add(path);
        SavePlaylistFolders(folders);
        LoadPlaylistSidebarItems();
    }

    internal async Task RenamePlaylistFolder(SidebarItem? folderNode)
    {
        if (folderNode is not { IsPlaylistFolder: true })
        {
            return;
        }

        var dialog = new Views.PlaylistNameDialog(folderNode.Name, title: "Rename Folder", prompt: "Folder name:");
        var result = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var oldPath = folderNode.FolderPath;
        var slash = oldPath.LastIndexOf('/');
        var newPath = PlaylistFolderSync.NormalizeFolder(slash < 0 ? result : oldPath[..(slash + 1)] + result);

        if (newPath.Length == 0 || string.Equals(newPath, oldPath, StringComparison.Ordinal))
        {
            return;
        }

        RewritePlaylistFolderPaths(oldPath, newPath);
    }

    /// <summary>Deletes the folder (and any subfolders); the playlists inside move to the root.
    /// The .m3u8 files were never anywhere else, so nothing on disk is deleted.</summary>
    internal void DeletePlaylistFolder(SidebarItem? folderNode)
    {
        if (folderNode is not { IsPlaylistFolder: true })
        {
            return;
        }

        RewritePlaylistFolderPaths(folderNode.FolderPath, null);
    }

    internal void MovePlaylistToFolder(SidebarItem? playlistItem, string? folder)
    {
        if (playlistItem?.PlaylistId is not int id)
        {
            return;
        }

        var target = PlaylistFolderSync.NormalizeFolder(folder);
        if (string.Equals(target, playlistItem.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MediaCache.SetPlaylistFolder(id, target);
        ExportPlaylistFile(id);
        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();
    }

    /// <summary>
    /// Renames (<paramref name="newPath"/> set) or dissolves (<paramref name="newPath"/> null) a
    /// folder subtree, updating every playlist under it, its file, and the saved empty folders.
    /// </summary>
    private void RewritePlaylistFolderPaths(string oldPath, string? newPath)
    {
        var prefix = oldPath + "/";

        string? Map(string current)
        {
            var isSelf = string.Equals(current, oldPath, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            if (newPath is null)
            {
                return null;
            }

            return isSelf ? newPath : newPath + current[oldPath.Length..];
        }

        foreach (var playlist in MediaCache.LoadAllPlaylists())
        {
            var folder = PlaylistFolderSync.NormalizeFolder(playlist.Folder);
            if (folder.Length == 0)
            {
                continue;
            }

            var target = Map(folder) ?? string.Empty;
            if (!string.Equals(target, folder, StringComparison.Ordinal))
            {
                MediaCache.SetPlaylistFolder(playlist.Id, target);
                ExportPlaylistFile(playlist.Id);
            }
        }

        SavePlaylistFolders(SavedPlaylistFolders()
            .Select(f => Map(PlaylistFolderSync.NormalizeFolder(f)))
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!));

        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();
    }

    [RelayCommand]
    internal async Task RenamePlaylist(SidebarItem? item)
    {
        if (item?.PlaylistId == null)
        {
            return;
        }

        var dialog = new Views.PlaylistNameDialog(item.Name);
        var result = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var previousName = item.Name;
        MediaCache.RenamePlaylist(item.PlaylistId.Value, result.Trim());
        if (!string.Equals(previousName, result.Trim(), StringComparison.Ordinal))
        {
            PlaylistFolderSync.Delete(App.FolderPath, previousName);
            MediaCache.SetPlaylistSourcePath(item.PlaylistId.Value, string.Empty);
        }
        ExportPlaylistFile(item.PlaylistId.Value);
        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();
    }

    /// <summary>
    /// "Import Audiobooks..." (the Audiobooks sidebar entry's context menu): picks files the user
    /// already owns and copies them into {library}/.audiobooks/{Author}/{Book}/ - where LOCATION
    /// makes them audiobooks regardless of tagging - then folds them in with a delta scan.
    /// </summary>
    internal async Task ImportAudiobooksAsync()
    {
        if (string.IsNullOrWhiteSpace(App.FolderPath))
        {
            UpdateMainStatus("Set a library folder first.");
            return;
        }

        var files = await _window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import Audiobooks",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Audiobook Files") { Patterns = ["*.m4b", "*.m4a", "*.mp3", "*.aac"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        int copied = 0, skipped = 0;
        await Task.Run(() =>
        {
            foreach (var picked in files)
            {
                var source = picked.Path.LocalPath;
                if (!FileScanner.IsSupportedExtension(source))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var dest = AudiobookDownloadService.ImportDestinationFor(App.FolderPath, source);
                    if (File.Exists(dest))
                    {
                        skipped++;   // already imported - importing twice shouldn't duplicate
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(source, dest);
                    copied++;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Audiobook import failed for {Source}", source);
                    skipped++;
                }
            }
        });

        _log.Information("Audiobook import: {Copied} copied, {Skipped} skipped", copied, skipped);
        UpdateMainStatus(skipped == 0
            ? $"Imported {copied} audiobook file(s)."
            : $"Imported {copied} audiobook file(s), skipped {skipped}.");
        if (copied > 0)
        {
            await ScanAndAnalyzeLibraryAsync();
        }
    }

    [RelayCommand]
    internal async Task ImportPlaylist()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import Playlist",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Playlist Files") { Patterns = ["*.m3u", "*.m3u8", "*.pls", "*.xspf"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var filePath = files[0].Path.LocalPath;
        var result = PlaylistImporter.Import(filePath);
        if (result.TrackPaths.Count == 0)
        {
            return;
        }

        // Match tracks to library by file path
        var libraryLookup = _allItems
            .Where(i => i.FilePath != null)
            .ToDictionary(i => i.FilePath!, StringComparer.OrdinalIgnoreCase);

        var matched = new List<MediaItem>();
        var unmatched = new List<string>();

        foreach (var path in result.TrackPaths)
        {
            if (libraryLookup.TryGetValue(path, out var item))
            {
                matched.Add(item);
            }
            else if (File.Exists(path) && FileScanner.IsSupportedExtension(path))
            {
                unmatched.Add(path);
            }
        }

        // If there are unmatched tracks that exist on disk, offer to copy them
        if (unmatched.Count > 0)
        {
            var copyDialog = new Views.ConfirmDialog(
                "Copy to Library",
                $"{unmatched.Count} track(s) are not in your library but exist on disk.\n\nCopy them to your music folder?",
                "Copy");
            var doCopy = await copyDialog.ShowDialog<bool>(_window);

            if (doCopy)
            {
                // Copy + analyze + upsert off the UI thread - importing a playlist's worth
                // of tracks is seconds of file I/O and TagLib work per track, and it used
                // to run inline on the dispatcher. Only the _allItems join comes back.
                var imported = await Task.Run(() =>
                {
                    var results = new List<MediaItem>();
                    foreach (var sourcePath in unmatched)
                    {
                        var destPath = Path.Combine(App.FolderPath, Path.GetFileName(sourcePath));
                        try
                        {
                            if (!File.Exists(destPath))
                            {
                                File.Copy(sourcePath, destPath);
                            }

                            var newItem = FileScanner.CreateMediaItemFromPath(destPath);
                            if (newItem != null)
                            {
                                AudioFileAnalyzer.AnalyzeFile(newItem);
                                results.Add(newItem);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "Failed to copy {Source} into the library", sourcePath);
                        }
                    }

                    MediaCache.UpsertMusicBatch(results);
                    return results;
                });

                foreach (var newItem in imported)
                {
                    _allItems.Add(newItem);
                    matched.Add(newItem);
                }

                _log.Information("Copied {Count} track(s) into the library", imported.Count);
            }
        }

        if (matched.Count == 0)
        {
            return;
        }

        // Ask for playlist name
        var name = !string.IsNullOrWhiteSpace(result.Name) ? result.Name : Path.GetFileNameWithoutExtension(filePath);
        var nameDialog = new Views.PlaylistNameDialog(name);
        var chosenName = await nameDialog.ShowDialog<string?>(_window);
        if (string.IsNullOrWhiteSpace(chosenName))
        {
            return;
        }

        var importSource = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".m3u8" => "M3U8",
            ".m3u"  => "M3U",
            ".pls"  => "PLS",
            ".xspf" => "XSPF",
            _       => "Imported",
        };
        var playlistId = MediaCache.CreatePlaylist(chosenName.Trim(), importSource);
        foreach (var track in matched)
        {
            MediaCache.AddTrackToPlaylist(playlistId, track.Id);
        }

        ExportPlaylistFile(playlistId);

        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();

        var newPlaylistItem = PlaylistItems.FirstOrDefault(i => i.PlaylistId == playlistId);
        if (newPlaylistItem != null)
        {
            SelectedSidebarItem = newPlaylistItem;
        }
    }

    /// <summary>
    /// Writes a playlist out to the music folder. Discovered playlists are rewritten in place;
    /// the rest land in the root as &lt;name&gt;.m3u8. Never throws - a read-only or missing
    /// music folder must not break the edit that triggered it.
    /// </summary>
    private void ExportPlaylistFile(int playlistId)
    {
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        try
        {
            var playlist = MediaCache.LoadAllPlaylists().FirstOrDefault(p => p.Id == playlistId);
            if (playlist is null)
            {
                return;
            }

            var tracks = GetPlaylistMediaItems(playlistId);
            var target = string.IsNullOrEmpty(playlist.SourcePath)
                ? PlaylistFolderSync.PathFor(App.FolderPath, playlist.Name)
                : playlist.SourcePath;

            PlaylistFolderSync.WriteTo(target, App.FolderPath, playlist.Name, tracks, playlist.Folder);

            // Claim the file, or the next scan discovers it as a playlist OrgZ has never seen
            // and adds a second row for it.
            if (!string.Equals(playlist.SourcePath, target, StringComparison.OrdinalIgnoreCase))
            {
                MediaCache.SetPlaylistSourcePath(playlistId, target);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not write the playlist file for {PlaylistId}", playlistId);
        }
    }

    /// <summary>
    /// Rewrites Favorites.m3u8 from the favourite flags. Write-only - the file is never read
    /// back, so the flags stay the single source of truth.
    /// </summary>
    internal void ExportFavoritesFile()
    {
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        try
        {
            var favorites = _allItems
                .Where(i => i.IsFavorite && IsLocalLibraryFile(i))
                .ToList();

            PlaylistFolderSync.Write(App.FolderPath, PlaylistFolderSync.FavoritesName, favorites);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not write Favorites.m3u8");
        }
    }

    /// <summary>
    /// Marks a whole selection as favourite - the drop-on-the-star gesture. Already-favourite
    /// tracks are left alone rather than toggled off, so dropping a mixed selection adds them
    /// all instead of inverting each one.
    /// </summary>
    internal void FavoriteTracks(IReadOnlyList<MediaItem> items)
    {
        var added = 0;

        foreach (var item in items.Where(i => !i.IsFavorite))
        {
            ToggleFavorite(item);
            added++;
        }

        if (added > 0)
        {
            UpdateMainStatus(added == 1 ? "Added 1 track to Favorites." : $"Added {added} tracks to Favorites.");
        }
    }

    internal List<MediaItem> GetPlaylistMediaItems(int playlistId, Dictionary<string, MediaItem>? lookup = null)
    {
        var trackIds = MediaCache.GetPlaylistTrackIds(playlistId);
        // Callers on a background thread must pass a lookup built via BuildItemLookup() on the
        // UI thread; the null-default path enumerates _allItems here and is only safe on the UI thread.
        lookup ??= BuildItemLookup();
        return trackIds.Where(lookup.ContainsKey).Select(id => lookup[id]).ToList();
    }

    /// <summary>
    /// A snapshot id→item map of the library. Must be built on the UI thread: it enumerates the
    /// UI-bound <c>_allItems</c>, and reading an ObservableCollection from a threadpool thread
    /// while the UI may be mutating it throws "collection was modified". Pass the result into any
    /// <see cref="GetPlaylistMediaItems"/> call that runs inside a <see cref="Task.Run"/>.
    /// </summary>
    private Dictionary<string, MediaItem> BuildItemLookup()
        => _allItems.Where(i => i.FilePath != null).ToDictionary(i => i.Id);

    /// <summary>
    /// Snapshot of currently connected devices - safe to enumerate from the view layer
    /// without holding a reference to the live _connectedDevices dictionary.
    /// </summary>
    internal IReadOnlyList<ConnectedDevice> ConnectedDevicesSnapshot()
        => _connectedDevices.Values.ToList();

    /// <summary>
    /// Resolves the connected device a "Device:{mount}" sidebar node points at.
    /// </summary>
    private ConnectedDevice? DeviceForSidebarItem(SidebarItem? item)
    {
        var mount = ResolveDeviceMountPath(item?.ViewConfigKey, _connectedDevices.Keys);
        return mount is not null && _connectedDevices.TryGetValue(mount, out var dev) ? dev : null;
    }

    /// <summary>
    /// Resolves a sidebar <c>ViewConfigKey</c> to the mount path of the device it belongs to, or null
    /// when it isn't a device view. The device's Podcasts/Audiobooks child views suffix the mount path
    /// with the media kind (<c>"Device:E:\:Podcast"</c>), so a bare exact lookup misses - this matches
    /// against the known mount paths (longest prefix wins) instead. Pulled out and made pure so the
    /// resolution is unit-tested: a miss here silently kills "Remove from iPod" and podcast sync from
    /// those sub-views (dev resolves null → the CRUD call no-ops), which is exactly how it regressed.
    /// </summary>
    internal static string? ResolveDeviceMountPath(string? viewConfigKey, IEnumerable<string> knownMountPaths)
    {
        if (viewConfigKey is not { } key || !key.StartsWith("Device:", StringComparison.Ordinal))
        {
            return null;
        }
        var rest = key["Device:".Length..];
        string? best = null;
        foreach (var mount in knownMountPaths)
        {
            if (string.Equals(rest, mount, StringComparison.OrdinalIgnoreCase))
            {
                return mount;   // the device's own root node ("Device:{mount}")
            }
            if (rest.StartsWith(mount, StringComparison.OrdinalIgnoreCase) && (best is null || mount.Length > best.Length))
            {
                best = mount;   // a "{mount}:Podcast" / ":Audiobook" child view - longest match wins
            }
        }
        return best;
    }

    /// <summary>
    /// Whether a view/cache key belongs to the device at <paramref name="mountPath"/> - its root view
    /// ("Device:{mount}") or any sub-view ("Device:{mount}:Podcast", ":Audiobook", ":Playlist:{id}").
    /// Boundary-aware on purpose: unlike a bare prefix match, "Device:/media/ipod" does NOT claim
    /// "Device:/media/ipod-red"'s keys, so tearing one device down can't take a sibling's views with it.
    /// Drives the disconnect teardown (view-cache eviction + selection fallback).
    /// </summary>
    internal static bool IsDeviceViewKeyFor(string? viewConfigKey, string mountPath)
    {
        if (viewConfigKey is null)
        {
            return false;
        }
        var root = $"Device:{mountPath}";
        return string.Equals(viewConfigKey, root, StringComparison.OrdinalIgnoreCase)
            || viewConfigKey.StartsWith($"{root}:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether dragging <paramref name="item"/> onto the given device node is allowed: a Music
    /// item on any writable stock iPod, or an Audiobook when the tier carries audiobooks NATIVELY
    /// (media_type/media_kind 8) - a device without the concept refuses rather than mislabeling a
    /// book as a song. Other media kinds / device types reject so the drop cursor shows "no".
    /// </summary>
    internal bool CanAcceptMediaDrop(SidebarItem? deviceItem, MediaItem? item)
    {
        if (item is null || item.Kind is not (MediaKind.Music or MediaKind.Audiobook))
        {
            return false;
        }
        var dev = DeviceForSidebarItem(deviceItem);
        if (dev is not { DeviceType: DeviceType.StockIPod } || !IPodCapabilities.SupportsDatabaseWrite(dev.IpodGeneration))
        {
            return false;
        }
        return item.Kind != MediaKind.Audiobook || IPodDevice.For(dev).SupportsAudiobooks;
    }

    /// <summary>
    /// Whether the media right-click > Sync submenu should offer <paramref name="device"/> for
    /// <paramref name="item"/>: a local music/audiobook file (not one already on a device) whose
    /// tier can write it - any playlist-capable tier for music, an audiobook-capable tier for books.
    /// </summary>
    internal bool CanSyncItemToDevice(MediaItem item, ConnectedDevice device)
    {
        if (item.Kind is not (MediaKind.Music or MediaKind.Audiobook)
            || string.IsNullOrEmpty(item.FilePath)
            || item.Source?.StartsWith("device:", StringComparison.Ordinal) == true)
        {
            return false;
        }
        var ipod = IPodDevice.For(device);
        return item.Kind == MediaKind.Audiobook ? ipod.SupportsAudiobooks : ipod.SupportsTrackAdd;
    }

    /// <summary>
    /// Whether the item is already on the device (by artist+title) - the Sync submenu greys out
    /// such a device, the same "already there, adding is a no-op" cue the Add-to-Playlist submenu
    /// gives for the current playlist.
    /// </summary>
    /// <summary>
    /// The favorites that can actually be burned or synced: favorited MUSIC with a local file.
    /// Burn, sync-plan, mirror-prune and the playlist header each spelled this predicate out,
    /// so a change to what "syncable favorite" means (audiobooks? shared tracks?) had to be
    /// made five times or the four surfaces would quietly disagree about the same playlist.
    /// </summary>
    private List<MediaItem> FavoriteMusicFiles()
        => _allItems.Where(i => i.IsFavorite && i.Kind == MediaKind.Music && !string.IsNullOrEmpty(i.FilePath)).ToList();

    internal bool IsItemAlreadyOnDevice(MediaItem item, ConnectedDevice device)
        => DeviceMatcherFor(device).Contains(item);

    // Per-device "already here" matchers. Building the Sync submenu asks this once per device per
    // SELECTED ROW, and the old scan walked all of _allItems for each of those questions - so
    // opening a context menu over a big library with a device attached was O(rows x devices)
    // string work. Keyed by mount + the library's version counter, so a stale one can't outlive
    // an edit.
    private readonly Dictionary<string, (int Version, DeviceTrackIdentity.DeviceMatcher Matcher)> _deviceMatchers = new(StringComparer.Ordinal);

    private DeviceTrackIdentity.DeviceMatcher DeviceMatcherFor(ConnectedDevice device)
    {
        var source = $"device:{device.MountPath}";
        if (_deviceMatchers.TryGetValue(source, out var cached) && cached.Version == _dataVersion)
        {
            return cached.Matcher;
        }

        var matcher = new DeviceTrackIdentity.DeviceMatcher(_allItems.Where(i => i.Source == source));
        _deviceMatchers[source] = (_dataVersion, matcher);
        return matcher;
    }

    /// <summary>
    /// Media right-click > Sync > (device): imports one track onto the device through its tier
    /// backend (media_type auto-detected for audiobooks). Skips a track already there by
    /// artist+title, and never creates a playlist - the single item just joins the device library.
    /// </summary>
    internal async Task SyncItemToDeviceAsync(MediaItem item, ConnectedDevice device)
    {
        if (!CanSyncItemToDevice(item, device) || !File.Exists(item.FilePath!))
        {
            return;
        }

        var ffmpeg = ResolveFfmpeg();
        if (device.DeviceType == DeviceType.StockIPod && ffmpeg is null)
        {
            UpdateMainStatus("ffmpeg wasn't found — needed to transcode for the iPod.");
            return;
        }

        var deviceSource = $"device:{device.MountPath}";
        if (DeviceMatcherFor(device).Contains(item))
        {
            UpdateMainStatus($"“{item.Title}” is already on {device.Name}.");
            return;
        }

        var ipod = IPodDevice.For(device);
        var (ct, owns) = BeginSyncScope();
        BeginLcdBusy($"Syncing to {device.Name}");
        try
        {
            // Block-scoped using (not `using var`): opening the batch and the commit its Dispose
            // performs both belong to this try, so a mount that dies mid-copy is reported here
            // instead of unwinding out of the async void drop handler and killing the process.
            using (var batch = ipod.BeginBatchWrite())
            {
                var deviceItem = await AddTrackToDeviceCoreAsync(ipod, item, ffmpeg ?? "ffmpeg", ct);
            }

            IPodArtworkReader.Invalidate(device.MountPath);
            device.SetSpaceFrom(_allItems.Where(i => i.Source == deviceSource));
            _log.Information("Synced “{Title}” to {Device}", item.Title, device.MountPath);
            UpdateMainStatus($"Synced “{item.Title}” to {device.Name}.");
        }
        catch (OperationCanceledException)
        {
            if (!owns)
            {
                throw;   // the outer gesture owns the cancelled-messaging
            }
            UpdateMainStatus($"Sync to {device.Name} cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to sync {Track} to {Device}", item.FilePath, device.MountPath);
            UpdateMainStatus($"Couldn't sync “{item.Title}” to {device.Name} — {ex.Message}");
        }
        finally
        {
            EndSyncScope(owns);
            EndLcdBusy();
        }
    }

    /// <summary>
    /// THE one way a track's bytes land on a device - drag-drop, right-click Sync, playlist sync,
    /// full device sync, and audiobook sync all funnel here so the gestures can never diverge again.
    /// The LCD detail row shows the live phase + track title ("Transcoding “X”…" / "Copying “X”…")
    /// with that phase's true 0..1 bar; batch callers pass index/count for a "(i/N) " prefix - the
    /// only textual difference between a single add and a batch. Cancellation-aware end to end: the
    /// LCD Cancel X trips the token, the tier aborts mid-transcode/mid-copy and deletes its torn
    /// output, and nothing joins the live view.
    /// </summary>
    private async Task<MediaItem> AddTrackToDeviceCoreAsync(IPodDevice ipod, MediaItem track, string ffmpeg, CancellationToken ct, int index = 0, int count = 1, string? preparedFile = null)
    {
        var prefix = count > 1 ? $"({index}/{count}) " : string.Empty;
        var title = track.Title ?? track.FileName ?? "track";

        // Analyze-at-sync: a track crossing to a device gets its loudness measured if it never was.
        // The LIBRARY file is tagged (permanent - the library analyzes itself through use), and the
        // tier picks the value up for the transcoded copy's tag + the device row's soundcheck field.
        if (track.ReplayGainTrackGainDb is null && !string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath)
            && track.Source?.StartsWith("device:", StringComparison.Ordinal) != true)
        {
            SetLcdBusy($"{prefix}Analyzing “{title}”…", 0);
            ct.ThrowIfCancellationRequested();
            var gain = await ReplayGainService.ComputeAndTagAsync(track.FilePath, ffmpeg, ct);
            if (gain is { } g)
            {
                track.ReplayGainTrackGainDb = g;
                FireAndForget(Task.Run(() => MediaCache.UpdateReplayGain(track.Id, g), CancellationToken.None), "replay-gain persist");
            }
        }

        SetLcdBusy(prefix + (ipod.WillTranscode(track) ? $"Transcoding “{title}”…" : $"Copying “{title}”…"), 0);
        var deviceItem = await ipod.AddTrackAsync(track, ffmpeg,
            (stage, f) => SetLcdBusy(prefix + (stage == "transcode" ? $"Transcoding “{title}”…" : $"Copying “{title}”…"), f), ct, preparedFile);
        _allItems.Add(deviceItem);
        AddToLiveView(deviceItem);

        // Nudge the capacity bar per landed track - a long sync otherwise reads "Audio 0 B"
        // until the end-of-sync SetSpaceFrom, which stays the authority.
        ipod.Device.AdjustSpaceFor(deviceItem, deviceItem.FileSize ?? 0);
        return deviceItem;
    }

    /// <summary>
    /// The LCD title for a job the service was already running when we launched. Pure so
    /// the wording is tested; unknown kinds still read as something rather than crashing
    /// a startup path.
    /// </summary>
    internal static string DescribeResumedJob(Services.DeviceHelper.JobsServiceOps.RunningJob job) => job.Kind switch
    {
        "disc" => "Burning (in progress)",
        "sync" => job.Target is { Length: > 0 } mount ? $"Syncing {mount} (in progress)" : "Syncing (in progress)",
        _ => "Working (in progress)",
    };

    /// <summary>
    /// Reattaches to work the background service kept running while OrgZ was closed: asks
    /// what's in flight, then follows the job's progress file so the LCD picks the
    /// operation back up mid-stream instead of showing an idle window over a live burn.
    /// </summary>
    internal async Task ReattachToServiceJobsAsync()
    {
        try
        {
            var jobs = await Services.DeviceHelper.DeviceHelperClient.RunningJobsAsync();
            if (jobs.Count == 0)
            {
                return;
            }

            // One LCD, so follow the first job; a second running job is rare (the service
            // gates disc and sync to one each) and its result still lands on the device.
            var job = jobs[0];
            _log.Information("Reattaching to service job: {Kind} ({Progress})", job.Kind, job.ProgressPath);

            BeginLcdBusy(DescribeResumedJob(job), "Reconnected");
            _burnWritePhase = job.Kind == "disc";   // a running burn is still uncancellable

            await FollowJobProgressAsync(job, _vmCts.Token);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "reattach to service jobs failed");
        }
        finally
        {
            _burnWritePhase = false;
            EndLcdBusy();
        }
    }

    private async Task FollowJobProgressAsync(Services.DeviceHelper.JobsServiceOps.RunningJob job, CancellationToken ct)
    {
        if (!File.Exists(job.ProgressPath))
        {
            return;
        }

        using var fs = new FileStream(job.ProgressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        // Cancellation is the VM's lifetime: this loop is started fire-and-forget from
        // LoadAsync, and without the token a service that never reports idle (or a hung
        // RPC) would keep it polling for the rest of the process.
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                // Caught up: the job is still going, so wait for more. Stop once the
                // service says it's idle - that's the authoritative end signal.
                var stillRunning = await Services.DeviceHelper.DeviceHelperClient.RunningJobsAsync();
                if (!stillRunning.Any(j => j.ProgressPath == job.ProgressPath))
                {
                    UpdateMainStatus(job.Kind == "disc" ? "Disc operation finished." : "Sync finished.");
                    return;
                }

                await Task.Delay(500, ct);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var evt = System.Text.Json.JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent);
                if (evt is { Type: "burn-progress" } && evt.TotalDiscSectors > 0)
                {
                    var pct = (double)evt.TotalSectorsWritten / evt.TotalDiscSectors;
                    SetLcdBusy($"Track {evt.TrackNumber}/{evt.TrackCount} — {(int)(pct * 100)}%", pct);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A sync job writes a different event shape; the reattach still shows
                // its LCD title and completion, just without a percentage.
            }
        }
    }

    /// <summary>
    /// Browses the LAN for OrgZ shares and reconciles the sidebar: newly-seen shares mount
    /// (catalogue fetched, tracks joined to the live list), vanished ones unmount cleanly.
    /// Best-effort and quiet - a LAN with no shares must cost nothing visible.
    /// </summary>
    internal async Task ScanForSharesAsync()
    {
        if (_shareScanning)
        {
            return;
        }

        _shareScanning = true;
        try
        {
            var found = await Services.Sharing.ShareDiscovery.BrowseAsync(TimeSpan.FromSeconds(2));

            // Never mount our own share back into our own sidebar. The advertiser
            // answers with a per-subnet address on a multi-homed host, so "our own"
            // means any of our interface addresses, not just the default-route one.
            var mine = Services.Sharing.MdnsAdvertiser.LocalInterfaceAddresses()
                .Select(i => i.Address.ToString())
                .ToHashSet(StringComparer.Ordinal);
            if (Services.Sharing.MdnsAdvertiser.LocalIPv4() is { } m)
            {
                mine.Add(m);
            }
            found.RemoveAll(s => s.Address is { } a && mine.Contains(a));

            var live = found.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var goneKey in _shareTracks.Keys.Where(k => !live.Contains(k)).ToList())
            {
                UnmountShare(goneKey);
            }

            foreach (var share in found.Where(s => !_shareTracks.ContainsKey(s.Key)))
            {
                var tracks = await Services.Sharing.ShareDiscovery.FetchCatalogueAsync(share);
                if (tracks.Count == 0)
                {
                    continue;
                }

                var playlists = await Services.Sharing.ShareDiscovery.FetchPlaylistsAsync(share);
                MountShare(share, tracks, playlists);
            }

            // Refresh stale catalogues on already-mounted shares: new tracks on the peer
            // used to appear only after it vanished from mDNS and came back. Remount only
            // when the content actually changed, so the sidebar doesn't churn every pass.
            foreach (var share in found.Where(s => _shareTracks.ContainsKey(s.Key)))
            {
                if (_shareFetchedAt.TryGetValue(share.Key, out var fetchedAt)
                    && DateTime.UtcNow - fetchedAt < ShareRefreshInterval)
                {
                    continue;
                }

                var tracks = await Services.Sharing.ShareDiscovery.FetchCatalogueAsync(share);
                _shareFetchedAt[share.Key] = DateTime.UtcNow;
                if (tracks.Count == 0)
                {
                    continue;   // transient fetch failure - keep the catalogue we have
                }

                var current = _shareTracks[share.Key];
                var unchanged = current.Count == tracks.Count
                    && current.Select(t => t.Id).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(tracks.Select(t => t.Id));
                if (unchanged)
                {
                    continue;
                }

                var playlists = await Services.Sharing.ShareDiscovery.FetchPlaylistsAsync(share);
                UnmountShare(share.Key);
                MountShare(share, tracks, playlists);
                _log.Information("Refreshed share {Key}: now {Count} track(s)", share.Key, tracks.Count);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "share scan failed");
        }
        finally
        {
            _shareScanning = false;
        }
    }

    /// <summary>
    /// Mounts a share from supplied data instead of the network. Docs screenshot harness only -
    /// a real share arrives through mDNS discovery.
    /// </summary>
    internal void MountShareForScreenshots(
        Services.Sharing.DiscoveredShare share,
        List<MediaItem> tracks,
        List<Services.Sharing.ShareDiscovery.SharePlaylist> playlists)
        => MountShare(share, tracks, playlists);

    private void MountShare(Services.Sharing.DiscoveredShare share, List<MediaItem> tracks, List<Services.Sharing.ShareDiscovery.SharePlaylist> playlists)
    {
        var viewKey = $"Share:{share.Key}";
        ListViewConfigs.Register(viewKey, ListViewConfigs.BuildShareConfig(share.Key));

        _shareTracks[share.Key] = tracks;
        _shareFetchedAt[share.Key] = DateTime.UtcNow;
        _allItems.AddRange(tracks);

        var node = new SidebarItem
        {
            Name = share.Name,
            Icon = "fa-solid fa-network-wired",
            Category = "SHARES",
            IsEnabled = true,
            ViewConfigKey = viewKey,
        };

        // The remote's playlists hang under the share the way a device's do. Each is an
        // ordered-ids view over the mounted tracks; Import (sidebar context menu) copies
        // it - files and all - into the local library.
        for (var i = 0; i < playlists.Count; i++)
        {
            var playlistKey = $"SharePlaylist:{share.Key}:{i}";
            ListViewConfigs.Register(playlistKey, ListViewConfigs.BuildSharePlaylistConfig(playlistKey, playlists[i].TrackIds));
            _sharePlaylists[playlistKey] = playlists[i];

            node.Children.Add(new SidebarItem
            {
                Name = playlists[i].Name,
                // The remote's Favorites wears the same star it wears at home, keyed
                // off the wire's playlist TYPE - a remote's ordinary playlist that
                // happens to be called "Favorites" stays a plain list.
                Icon = playlists[i].IsFavorites ? "fa-solid fa-star" : "fa-solid fa-list-ul",
                Category = "SHARES",
                IsEnabled = true,
                ViewConfigKey = playlistKey,
            });
        }

        ShareItems.Add(node);

        _log.Information("Mounted share \"{Name}\" ({Key}) with {Count} track(s), {Playlists} playlist(s)", share.Name, share.Key, tracks.Count, playlists.Count);
        ApplyFilter();
    }

    /// <summary>
    /// Copies share tracks into the local library: each is downloaded over its stream
    /// URL into the music folder and imported through the normal scan pipeline, so tags,
    /// analysis and the database row all come out exactly as a local file's would.
    /// Returns share-id → imported local id for the tracks that made it (a track whose
    /// filename already exists locally is skipped and simply not in the map).
    /// </summary>
    internal async Task<Dictionary<string, string>> ImportShareTracksToLibraryAsync(IReadOnlyList<MediaItem> shareTracks)
    {
        var imported = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = 0;

        foreach (var track in shareTracks.Where(Services.Sharing.ShareDiscovery.IsShareItem))
        {
            try
            {
                // Inside the try: a hostile catalogue whose path is rejected fails this one
                // track (logged and counted) instead of ending the whole import.
                var destPath = LibraryDestinationFor(track);

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                if (!File.Exists(destPath) && !await Services.Sharing.ShareDiscovery.DownloadTrackAsync(track, destPath))
                {
                    failures++;
                    continue;
                }

                var newItem = FileScanner.CreateMediaItemFromPath(destPath);
                if (newItem is null)
                {
                    failures++;
                    continue;
                }

                // Analysis (TagLib) + the DB write go to the pool: this loop runs per
                // imported track between awaits, so inline they stuttered the window
                // for the whole import.
                await Task.Run(() =>
                {
                    AudioFileAnalyzer.AnalyzeFile(newItem);
                    MediaCache.UpsertMusic(newItem);
                });

                if (_allItems.All(i => i.Id != newItem.Id))
                {
                    _allItems.Add(newItem);
                }

                imported[track.Id] = newItem.Id;
            }
            catch (Exception ex)
            {
                failures++;
                _log.Warning(ex, "Share import failed for {Title}", track.Title);
            }
        }

        _log.Information("Imported {Count} share track(s) into the library ({Failures} failed)", imported.Count, failures);
        ApplyFilter();
        return imported;
    }

    /// <summary>
    /// The library path a share track copies to - the same layout a CD rip and an iPod
    /// sync-to-library use: {Music}/{Artist}/{Album}/{NN - Title}.ext, deduped with a
    /// " (2)" suffix. A flat name at the library root (the old behaviour) sorted nowhere.
    ///
    /// Every part of the name arrives over the wire from the share, so every part is
    /// sanitised - the extension included, which is the one component that isn't a tag -
    /// and the finished path is asserted to still be inside the library folder. A share
    /// must not be able to steer a download anywhere else the user can write.
    /// </summary>
    internal static string LibraryDestinationFor(MediaItem track)
    {
        var artist = LibrarySegment(track.Artist, "Unknown Artist");
        var album = LibrarySegment(track.Album, "Unknown Album");
        var destDir = Path.Combine(App.FolderPath, artist, album);

        var title = string.IsNullOrWhiteSpace(track.Title) ? track.Id : track.Title!;
        var baseName = LibrarySegment(track.Track is { } trackNo && trackNo > 0 ? $"{trackNo:00} - {title}" : title, "Unknown");
        var ext = Services.Sharing.ShareDiscovery.SafeExtension(track.Extension) ?? ".mp3";

        var dest = Path.Combine(destDir, baseName + ext);
        for (var n = 2; File.Exists(dest); n++)
        {
            dest = Path.Combine(destDir, $"{baseName} ({n}){ext}");
        }

        if (!IsInsideLibraryFolder(dest))
        {
            throw new InvalidOperationException($"Destination “{dest}” is outside the library folder.");
        }

        return dest;
    }

    /// <summary>
    /// One sanitised segment of the library layout, never empty and never a relative step.
    /// A remote catalogue supplies these, and a segment that can climb ("..") or vanish
    /// would move the write out of the artist/album folder it is supposed to land in.
    /// </summary>
    private static string LibrarySegment(string? value, string fallback)
    {
        var safe = SanitizeFolderName(string.IsNullOrWhiteSpace(value) ? fallback : value!);
        return safe.Length == 0 || safe is "." or ".." ? fallback : safe;
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves to somewhere under the library folder.
    /// Case folding follows the platform: Linux paths are case-sensitive, Windows and macOS
    /// are not, and folding on Linux would let "/home/fox/music" pass for "/home/fox/Music".
    /// </summary>
    private static bool IsInsideLibraryFolder(string path)
    {
        try
        {
            var root = Path.GetFullPath(App.FolderPath);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return Path.GetFullPath(path).StartsWith(root, comparison);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't resolve {Path} against the library folder", path);
            return false;
        }
    }

    /// <summary>
    /// Imports a remote playlist: copies its tracks into the library (skipping any that
    /// already came over), then recreates the playlist locally with the same name and
    /// order. The sidebar picks the new playlist up immediately.
    /// </summary>
    internal async Task ImportSharePlaylistAsync(SidebarItem playlistNode)
    {
        if (playlistNode.ViewConfigKey is not { } key || !_sharePlaylists.TryGetValue(key, out var playlist))
        {
            return;
        }

        // The playlist's ids are namespaced share ids - resolve them to the mounted
        // MediaItems, preserving the remote order.
        var byId = _allItems.Where(Services.Sharing.ShareDiscovery.IsShareItem).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var tracks = playlist.TrackIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (tracks.Count == 0)
        {
            return;
        }

        var imported = await ImportShareTracksToLibraryAsync(tracks);

        var playlistId = MediaCache.CreatePlaylist(playlist.Name, "Share");
        foreach (var shareId in playlist.TrackIds)
        {
            if (imported.TryGetValue(shareId, out var localId))
            {
                MediaCache.AddTrackToPlaylist(playlistId, localId);
            }
        }

        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();
        _log.Information("Imported share playlist \"{Name}\" with {Count} track(s)", playlist.Name, imported.Count);
    }

    private void UnmountShare(string shareKey)
    {
        if (_shareTracks.Remove(shareKey, out var tracks))
        {
            foreach (var track in tracks)
            {
                _allItems.Remove(track);
            }
        }

        var playlistPrefix = $"SharePlaylist:{shareKey}:";
        foreach (var stale in _sharePlaylists.Keys.Where(k => k.StartsWith(playlistPrefix, StringComparison.Ordinal)).ToList())
        {
            _sharePlaylists.Remove(stale);
        }

        Services.Sharing.ShareStreamRelay.Unregister(shareKey);

        var viewKey = $"Share:{shareKey}";
        if (ShareItems.FirstOrDefault(i => i.ViewConfigKey == viewKey) is { } item)
        {
            ShareItems.Remove(item);
        }

        // Viewing the share (or one of its playlists) that just vanished? Fall back to
        // the library rather than leaving a dead view selected.
        if (LibraryItems.Count > 0
            && SelectedSidebarItem?.ViewConfigKey is { } selectedKey
            && (selectedKey == viewKey || selectedKey.StartsWith(playlistPrefix, StringComparison.Ordinal)))
        {
            SelectedSidebarItem = LibraryItems[0];
        }

        _log.Information("Unmounted share {Key}", shareKey);
        ApplyFilter();
    }

    /// <summary>
    /// Offers a multi-track sync to the background service, which owns the work from
    /// there - it keeps running if the GUI closes. Opt-in via Settings > Services
    /// (OrgZ.Services.KeepAlive.IPodSync, read by the caller and passed in - keeps this
    /// free of global state); any refusal returns false and the caller syncs in-process
    /// exactly as before.
    /// </summary>
    internal static async Task<bool> TryHandOffSyncToServiceAsync(string mountPath, IReadOnlyList<MediaItem> tracks, Func<string, IReadOnlyList<string>, Task<bool>> handOff, bool keepAliveEnabled)
    {
        if (!keepAliveEnabled)
        {
            return false;
        }

        var ids = tracks.Select(t => t.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
        if (ids.Count == 0)
        {
            return false;
        }

        return await handOff(mountPath, ids);
    }

    /// <summary>
    /// Cancellation scope for a sync gesture. The OUTERMOST caller creates the token (owns = true)
    /// and clears it in <see cref="EndSyncScope"/>; nested syncs (a full device sync running its
    /// playlists) reuse it - so one press of the LCD Cancel X stops the whole gesture, not just the
    /// sub-sync that happened to be running.
    /// </summary>
    private (CancellationToken Token, bool Owns) BeginSyncScope()
    {
        if (_deviceSyncCts != null)
        {
            return (_deviceSyncCts.Token, false);
        }
        _deviceSyncCts = new CancellationTokenSource();
        return (_deviceSyncCts.Token, true);
    }

    private void EndSyncScope(bool owns)
    {
        if (owns)
        {
            _deviceSyncCts?.Dispose();
            _deviceSyncCts = null;
        }
    }

    /// <summary>Right-click "Remove from iPod": deletes the item - any media kind - from the connected
    /// device via the per-tier backend (Nano 5G SQLite, binary iTunesDB, or Rockbox filesystem): its
    /// database rows and its audio file, then drops it from the live list. Reports loudly when a tier
    /// has no remove backend.</summary>
    /// <summary>Removes a whole selection from the device, sequentially - each track keeps
    /// the singular path's space accounting and per-track error reporting.</summary>
    internal async Task RemoveFromDeviceAsync(IReadOnlyList<MediaItem> tracks)
    {
        foreach (var track in tracks)
        {
            await RemoveFromDeviceAsync(track);
        }
    }

    internal async Task RemoveFromDeviceAsync(MediaItem? track)
    {
        if (track is null)
        {
            return;
        }
        var dev = DeviceForSidebarItem(SelectedSidebarItem);
        if (dev is null)
        {
            return;
        }

        try
        {
            await IPodDevice.For(dev).RemoveTrackAsync(track);

            _allItems.Remove(track);
            if (track.FileSize is { } removedSize && removedSize > 0)
            {
                dev.AdjustSpaceFor(track, -removedSize);
            }
            IPodArtworkReader.Invalidate(dev.MountPath);
            RemoveFromLiveView(track);
            UpdateMainStatus($"Removed “{track.Title}” from {dev.Name}.");
        }
        catch (NotImplementedException)
        {
            UpdateMainStatus($"Removing isn't supported on {dev.Name}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to remove {Track} from {Device}", track.FilePath, dev.MountPath);
            UpdateMainStatus($"Couldn't remove “{track.Title}”: {ex.Message}");
        }
    }

    /// <summary>
    /// Right-click "Rename…" on a device: writes the new name the iTunes way - DeviceInfo file on a
    /// stock iPod (the authoritative, unclipped name) plus the volume label as a mirror (FAT32 clips
    /// it at 11 chars) - then re-fingerprints so the sidebar and info bar pick it up live.
    /// </summary>
    internal async Task RenameDeviceAsync(SidebarItem? item)
    {
        var dev = DeviceForSidebarItem(item);
        if (dev is null || item is null)
        {
            return;
        }

        var dialog = new Views.PlaylistNameDialog(dev.Name, title: "Rename Device", prompt: "Device name:");
        var result = await dialog.ShowDialog<string?>(_window);
        if (string.IsNullOrWhiteSpace(result) || result.Trim() == dev.Name)
        {
            return;
        }
        var name = result.Trim();

        try
        {
            await Task.Run(() =>
            {
                if (dev.DeviceType == DeviceType.StockIPod)
                {
                    IPodRename.WriteName(dev.MountPath, name);

                    // iTunes shows the iTunesDB master playlist's name as the DEVICE name (that's
                    // why a renamed iPod kept its old identity there) - rename it in step.
                    var dbPath = IPodPaths.ITunesDb(dev.MountPath);
                    if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
                    {
                        var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
                        ITunesDbWriter.RenameMasterPlaylists(doc, name);
                        IPodTrackImporter.CommitDb(doc, dbPath, dev.MountPath, dev.IpodGeneration, dev.FireWireGuid);
                    }
                }
                IPodRename.TrySetVolumeLabel(dev.MountPath, name);
            });
            _log.Information("Renamed device {Mount} to {Name}", dev.MountPath, name);
            UpdateMainStatus($"Renamed to “{name}”.");
            RefreshDeviceInfo(item);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Rename failed for {Mount}", dev.MountPath);
            UpdateMainStatus($"Couldn't rename {dev.Name} — {ex.Message}");
        }
    }

    /// <summary>
    /// Drag-onto-device import: delegates to <see cref="SyncItemToDeviceAsync"/> so a drop and the
    /// right-click "Sync > (device)" are one code path - same duplicate check, same batch write,
    /// same space accounting, same progress surface. They diverged once (drag skipped the duplicate
    /// check and demanded ffmpeg even for Rockbox); never again.
    /// </summary>
    internal async Task ImportMediaToDeviceAsync(SidebarItem? deviceItem, MediaItem? track)
    {
        if (!CanAcceptMediaDrop(deviceItem, track) || track is null)
        {
            return;
        }
        await SyncItemToDeviceAsync(track, DeviceForSidebarItem(deviceItem)!);
    }



    /// <summary>Locates ffmpeg on PATH, then a bundled copy next to the app.</summary>
    private static string? ResolveFfmpeg() => ExecutableResolver.Find("ffmpeg");

    /// <summary>
    /// Sends a specific playlist (or Favorites) to a connected device from the sidebar's "send to device"
    /// entry. Resolves the tracks, then hands off to the tier-agnostic <see cref="SyncPlaylistToDeviceAsync"/>
    /// (copy/transcode + native playlist) - one path for stock iPods and Rockbox alike.
    /// </summary>
    internal async Task SendPlaylistToDevice(SidebarItem playlistItem, ConnectedDevice device)
    {
        List<MediaItem> tracks;
        if (playlistItem.PlaylistId is int pid)
        {
            var lookup = BuildItemLookup();
            tracks = await Task.Run(() => GetPlaylistMediaItems(pid, lookup));
        }
        else if (playlistItem.IsFavorites)
        {
            tracks = FavoriteMusicFiles();
        }
        else
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(playlistItem.Name) ? "Playlist" : playlistItem.Name;
        await SyncPlaylistToDeviceAsync(name, tracks, device);
    }

    internal static string NormalizeMatchKey(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }
        return $"{(artist ?? "").Trim()}|{title.Trim()}";
    }

    internal static string ToDeviceRelativePath(string absoluteDevicePath, string mountPath)
    {
        // Strip the mount prefix so the M3U uses Rockbox-style absolute-to-device paths
        // ("/Music/Rush/Signals/01.mp3") rather than host-specific ones ("/run/media/...").
        if (absoluteDevicePath.StartsWith(mountPath, StringComparison.OrdinalIgnoreCase))
        {
            var rel = absoluteDevicePath[mountPath.Length..].Replace('\\', '/');
            return rel.StartsWith('/') ? rel : '/' + rel;
        }
        return absoluteDevicePath.Replace('\\', '/');
    }

    internal static string ToMountAbsolute(string deviceRelativePath, string mountPath)
    {
        var rel = deviceRelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(mountPath, rel));
    }

    internal static string SanitizeFileName(string name) => Helpers.SafeName.For(name, Helpers.SafeName.Style.Replace);

    internal async Task ExportPlaylist(SidebarItem item, string format)
    {
        if (!item.PlaylistId.HasValue)
        {
            return;
        }

        var tracks = GetPlaylistMediaItems(item.PlaylistId.Value);
        if (tracks.Count == 0)
        {
            return;
        }

        var extension = format switch
        {
            "M3U8" => "m3u8",
            "PLS" => "pls",
            "XSPF" => "xspf",
            _ => "m3u8"
        };

        var file = await _window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = $"Export Playlist — {item.Name}",
            SuggestedFileName = $"{item.Name}.{extension}",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(format) { Patterns = [$"*.{extension}"] }
            ]
        });

        if (file == null)
        {
            return;
        }

        var path = file.Path.LocalPath;

        switch (format)
        {
            case "M3U8":
            {
                PlaylistExporter.ExportM3U8(path, item.Name, tracks);
                break;
            }

            case "PLS":
            {
                PlaylistExporter.ExportPLS(path, item.Name, tracks);
                break;
            }

            case "XSPF":
            {
                PlaylistExporter.ExportXSPF(path, item.Name, tracks);
                break;
            }
        }
    }

    /// <summary>
    /// File > Export Library - writes the whole local music library out as a playlist file the user
    /// names (format from the chosen extension). Was a disabled placeholder; PlaylistExporter already
    /// does the work, so it's a real feature now. Audiobooks/podcasts/device/CD tracks are excluded -
    /// this is the music library, matching the Music view.
    /// </summary>
    [RelayCommand]
    internal async Task ExportLibrary()
    {
        var tracks = _allItems
            .Where(i => i.Kind == MediaKind.Music && i.Source == null && !string.IsNullOrEmpty(i.FilePath))
            .ToList();
        if (tracks.Count == 0)
        {
            UpdateMainStatus("Your library has no music to export.");
            return;
        }

        var file = await _window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Library",
            SuggestedFileName = "OrgZ Library.m3u8",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("M3U8") { Patterns = ["*.m3u8"] },
                new Avalonia.Platform.Storage.FilePickerFileType("PLS") { Patterns = ["*.pls"] },
                new Avalonia.Platform.Storage.FilePickerFileType("XSPF") { Patterns = ["*.xspf"] },
            ],
        });
        if (file is null)
        {
            return;
        }

        var path = file.Path.LocalPath;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pls": PlaylistExporter.ExportPLS(path, "OrgZ Library", tracks); break;
            case ".xspf": PlaylistExporter.ExportXSPF(path, "OrgZ Library", tracks); break;
            default: PlaylistExporter.ExportM3U8(path, "OrgZ Library", tracks); break;
        }

        _log.Information("Exported library: {Count} tracks -> {Path}", tracks.Count, path);
        UpdateMainStatus($"Exported {tracks.Count} tracks to {Path.GetFileName(path)}.");
    }

    [RelayCommand]
    internal async Task DeletePlaylist(SidebarItem? item)
    {
        if (item?.PlaylistId == null)
        {
            return;
        }

        var dialog = new Views.ConfirmDialog("Delete Playlist", $"Delete playlist \"{item.Name}\"?\n\nThis cannot be undone.", "Delete");
        var ok = await dialog.ShowDialog<bool>(_window);
        if (!ok)
        {
            return;
        }

        var key = item.ViewConfigKey;
        MediaCache.DeletePlaylist(item.PlaylistId.Value);
        PlaylistFolderSync.Delete(App.FolderPath, item.Name);
        ListViewConfigs.Remove(key);

        // Navigate away if we're viewing the deleted playlist
        if (SelectedSidebarItem == item)
        {
            SelectedSidebarItem = PlaylistItems.FirstOrDefault(i => i.IsFavorites) ?? LibraryItems[0];
        }

        PlaylistItems.Remove(item);
        PlaylistsChanged?.Invoke();
    }

    /// <summary>Adds a whole selection to a playlist in view order, refreshing the config once.</summary>
    internal void AddTracksToPlaylist(int playlistId, IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        for (int i = 0; i < items.Count - 1; i++)
        {
            MediaCache.AddTrackToPlaylist(playlistId, items[i].Id);
        }

        // The last add goes through the singular path so the config refresh,
        // view-cache invalidation, and live-view re-filter run exactly once.
        AddTrackToPlaylist(playlistId, items[^1]);
    }

    internal void AddTrackToPlaylist(int playlistId, MediaItem item)
    {
        MediaCache.AddTrackToPlaylist(playlistId, item.Id);
        ExportPlaylistFile(playlistId);

        // Refresh the playlist's config with updated track IDs
        var key = $"Playlist:{playlistId}";
        var trackIds = MediaCache.GetPlaylistTrackIds(playlistId);
        ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlistId, trackIds));

        // Kill the playlist's cached view: a view SWITCH never bumps the data version, so without
        // this a playlist cached while empty is served verbatim on the next visit - header counting
        // its real tracks over an empty grid.
        _viewCache.Remove(key);

        // Refresh view if currently viewing this playlist
        if (SelectedSidebarItem?.ViewConfigKey == key)
        {
            _activeViewConfig = ListViewConfigs.Get(key);
            ApplyFilter();
        }
    }

    internal void SetRating(MediaItem item, int? rating)
    {
        if (item.Kind != MediaKind.Music)
        {
            return;
        }

        item.Rating = rating;
        MediaCache.SetRating(item.Id, rating);
        PersistRatingToTag(item);
    }

    /// <summary>
    /// Ratings ride in the file too (POPM / vorbis RATING) - library.db is a cache, not
    /// the only copy. Local library files only: a device or share track's bytes belong to
    /// their own store, and streams have no file.
    /// </summary>
    private static void PersistRatingToTag(MediaItem item)
    {
        if (item.Kind is not (MediaKind.Music or MediaKind.Audiobook) || item.Source != null || string.IsNullOrEmpty(item.FilePath))
        {
            return;
        }

        var path = item.FilePath;
        var rating = item.Rating;
        TaskObserver.FireAndForget(Task.Run(() => Services.TagRating.WriteToFile(path, rating)), "rating tag write");
    }

    /// <summary>
    /// Deletes an audiobook from disk, with confirmation. A book in the managed
    /// .audiobooks/{Author}/{Title}/ layout deletes as a whole - every chapter/part file, however
    /// many rows it spans - because that's the unit the user thinks in; a loose audiobook file
    /// anywhere else deletes alone. The store's Downloaded state reads from disk, so the book
    /// flips back to Download on its next detail open.
    /// </summary>
    internal async Task DeleteAudiobookFromDiskAsync(MediaItem item)
    {
        if (item.Kind != MediaKind.Audiobook || string.IsNullOrEmpty(item.FilePath))
        {
            return;
        }

        var bookDir = AudiobookDetector.BookFolderFor(item.FilePath);
        var bookName = !string.IsNullOrWhiteSpace(item.Album) ? item.Album : (item.Title ?? item.FileName ?? "this audiobook");
        var fileCount = bookDir is not null && Directory.Exists(bookDir)
            ? Directory.EnumerateFiles(bookDir, "*.*", SearchOption.AllDirectories).Count(FileScanner.IsSupportedExtension)
            : 1;

        var dialog = new Views.ConfirmDialog(
            "Remove from Library",
            $"Remove “{bookName}” from your library?\n\nThis permanently deletes {fileCount} file(s) from disk. It cannot be undone.",
            "Delete");
        if (!await dialog.ShowDialog<bool>(_window))
        {
            return;
        }

        List<string> deleted;
        try
        {
            deleted = AudiobookDownloadService.DeleteFromDisk(item.FilePath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Audiobook delete failed for {Path}", item.FilePath);
            UpdateMainStatus($"Couldn't delete {bookName} — {ex.Message}");
            return;
        }

        // Drop the matching rows immediately (the folder watcher would also catch up, but the
        // grid shouldn't show ghost rows for however long that takes) and their cache entries.
        var deletedPaths = new HashSet<string>(deleted, StringComparer.OrdinalIgnoreCase);
        var removedItems = _allItems.Where(i => IsLocalLibraryFile(i) && deletedPaths.Contains(i.FilePath!)).ToList();
        foreach (var removed in removedItems)
        {
            _allItems.Remove(removed);
        }
        await Task.Run(() => MediaCache.RemoveLibraryFiles(removedItems.Select(i => i.Id)));
        RefreshAllPlaylistConfigs();
        ApplyFilter();
        UpdateMainStatus($"Deleted {bookName} — {deleted.Count} file(s) removed.");
    }

    /// <summary>
    /// Removes a local library file for good: confirm, delete from disk, drop the rows. One
    /// gesture, one meaning across every kind (Fox's spec) - an audiobook scopes to its whole
    /// book folder, music to the single file. Only local library files route through here;
    /// device tracks have Remove from iPod and CD tracks never carry the command. The old
    /// ignore-based soft remove is retired (the Ignored view still shows and restores anything
    /// ignored before the change).
    /// </summary>
    /// <summary>
    /// Bulk remove: one confirmation for the whole selection, then per-file deletes and a
    /// single cache/playlist/view refresh. Audiobooks are excluded from bulk (they're
    /// folder-level deletions with their own flow) - a single-item selection still routes
    /// through the singular path so its wording and audiobook handling stay intact.
    /// </summary>
    internal async Task RemoveFromLibraryAsync(IReadOnlyList<MediaItem> items)
    {
        var deletable = items.Where(IsLocalLibraryFile).ToList();
        if (deletable.Count == 0)
        {
            return;
        }

        if (deletable.Count == 1)
        {
            await RemoveFromLibraryAsync(deletable[0]);
            return;
        }

        deletable.RemoveAll(i => i.Kind == MediaKind.Audiobook);
        if (deletable.Count == 0)
        {
            return;
        }

        var confirm = new Views.ConfirmDialog(
            "Remove from Library",
            $"Remove {Count(deletable.Count, "track")} from your library?\n\nThis permanently deletes the files from disk. It cannot be undone.",
            "Delete");
        if (!await confirm.ShowDialog<bool>(_window))
        {
            return;
        }

        var removedIds = new List<string>();
        foreach (var item in deletable)
        {
            try
            {
                File.Delete(item.FilePath!);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Library file delete failed for {Path}", item.FilePath);
                continue;
            }

            _allItems.Remove(item);
            removedIds.Add(item.Id);
        }

        if (removedIds.Count == 0)
        {
            UpdateMainStatus("Couldn't delete the selected tracks.");
            return;
        }

        try
        {
            await Task.Run(() => MediaCache.RemoveLibraryFiles([.. removedIds]));
            RefreshAllPlaylistConfigs();
            ApplyFilter();
            UpdateMainStatus($"Deleted {Count(removedIds.Count, "track")}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Post-delete cleanup failed for bulk removal");
            UpdateMainStatus($"Deleted {Count(removedIds.Count, "track")}, but library cleanup hit an error.");
        }
    }

    internal async Task RemoveFromLibraryAsync(MediaItem item)
    {
        if (!IsLocalLibraryFile(item))
        {
            return;
        }

        if (item.Kind == MediaKind.Audiobook)
        {
            await DeleteAudiobookFromDiskAsync(item);
            return;
        }

        var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : item.FileName ?? "this track";
        var dialog = new Views.ConfirmDialog(
            "Remove from Library",
            $"Remove “{title}” from your library?\n\nThis permanently deletes the file from disk. It cannot be undone.",
            "Delete");
        if (!await dialog.ShowDialog<bool>(_window))
        {
            return;
        }

        try
        {
            File.Delete(item.FilePath!);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Library file delete failed for {Path}", item.FilePath);
            UpdateMainStatus($"Couldn't delete {title} — {ex.Message}");
            return;
        }

        _allItems.Remove(item);
        try
        {
            await Task.Run(() => MediaCache.RemoveLibraryFiles([item.Id]));
            RefreshAllPlaylistConfigs();
            ApplyFilter();
            UpdateMainStatus($"Deleted {title}.");
        }
        catch (Exception ex)
        {
            // The file is already off disk and out of the live list; a cache/playlist cleanup
            // failure must not crash the async-void caller - log and let the next scan reconcile.
            _log.Error(ex, "Post-delete cleanup failed for {Path}", item.FilePath);
            UpdateMainStatus($"Deleted {title}, but library cleanup hit an error.");
        }
    }

    /// <summary>
    /// Clears the ignored flag on the item. It re-appears in its natural view (Music, Favorites, etc.).
    /// Playlist memberships are NOT restored - they were deleted at ignore time.
    /// </summary>
    /// <summary>
    /// Sets the iTunes tick on a whole selection and persists it. Mixed selections all
    /// become <paramref name="isChecked"/> - the menu item says what it will do.
    /// </summary>
    internal void SetChecked(IReadOnlyList<MediaItem> items, bool isChecked)
    {
        foreach (var item in items)
        {
            item.IsChecked = isChecked;
            MediaCache.SetIgnored(item.Id, !isChecked);
        }
    }

    /// <summary>
    /// Re-reads every playlist's track set from the DB and rebuilds its ListViewConfig entry.
    /// Call this after operations that mutate playlist membership outside of direct playlist APIs.
    /// </summary>
    private void RefreshAllPlaylistConfigs()
    {
        var playlists = MediaCache.LoadAllPlaylists();
        foreach (var p in playlists)
        {
            var key = $"Playlist:{p.Id}";
            var trackIds = MediaCache.GetPlaylistTrackIds(p.Id);
            ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(p.Id, trackIds));
        }

        // If currently viewing a playlist, swap in the refreshed config so ApplyFilter uses it
        if (_activeViewConfig?.PlaylistId != null)
        {
            _activeViewConfig = ListViewConfigs.Get(_activeViewConfig.Key);
        }
    }

    [RelayCommand]
    internal void RemoveFromPlaylist()
    {
        RemoveTracksFromPlaylist(SelectedItem is { } selected ? [selected] : []);
    }

    /// <summary>Removes a whole selection from the active playlist with one rebuild + re-filter.</summary>
    internal void RemoveTracksFromPlaylist(IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0 || SelectedSidebarItem?.PlaylistId == null)
        {
            return;
        }

        var playlistId = SelectedSidebarItem.PlaylistId.Value;
        var scrollAnchor = GetScrollAnchor?.Invoke();

        foreach (var item in items)
        {
            MediaCache.RemoveTrackFromPlaylist(playlistId, item.Id);
        }

        ExportPlaylistFile(playlistId);

        var key = $"Playlist:{playlistId}";
        var trackIds = MediaCache.GetPlaylistTrackIds(playlistId);
        ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlistId, trackIds));
        _activeViewConfig = ListViewConfigs.Get(key);
        ApplyFilter();
        RestoreScrollAnchor?.Invoke(scrollAnchor);
    }

    /// <summary>
    /// Returns the active playlist ID if the current view is a playlist; null otherwise.
    /// Used by the view to enable drag-to-reorder.
    /// </summary>
    internal int? ActivePlaylistId => _activeViewConfig?.PlaylistId;

    /// <summary>
    /// Reorders a track within the currently-active playlist.
    /// fromIndex/toIndex are positions within the current FilteredItems list;
    /// <paramref name="insertBefore"/> places the moved track before (true) or after (false) the
    /// target - matching the insertion line the drag showed.
    /// </summary>
    internal void ReorderPlaylistTrack(int fromIndex, int toIndex, bool insertBefore = false)
    {
        if (_activeViewConfig?.PlaylistId == null)
        {
            return;
        }

        if (fromIndex < 0 || fromIndex >= FilteredItems.Count || toIndex < 0 || toIndex >= FilteredItems.Count || fromIndex == toIndex)
        {
            return;
        }

        var playlistId = _activeViewConfig.PlaylistId.Value;
        var scrollAnchor = GetScrollAnchor?.Invoke();

        // Move within current order then push the whole list back to DB.
        // Use the full DB order (not just filtered) so search-filtered reorders don't lose hidden tracks.
        var fullOrder = MediaCache.GetPlaylistTrackIds(playlistId);
        var movedItem = FilteredItems[fromIndex];
        var targetItem = FilteredItems[toIndex];

        var fromDbIdx = fullOrder.IndexOf(movedItem.Id);
        if (fromDbIdx < 0 || fullOrder.IndexOf(targetItem.Id) < 0)
        {
            return;
        }

        fullOrder.RemoveAt(fromDbIdx);
        int insertIdx = fullOrder.IndexOf(targetItem.Id);
        if (!insertBefore)
        {
            insertIdx++;
        }
        fullOrder.Insert(Math.Clamp(insertIdx, 0, fullOrder.Count), movedItem.Id);

        MediaCache.ReorderPlaylistTracks(playlistId, fullOrder);
        ExportPlaylistFile(playlistId);

        var key = $"Playlist:{playlistId}";
        ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlistId, fullOrder));
        _activeViewConfig = ListViewConfigs.Get(key);
        MoveWithinLiveView(movedItem, targetItem, insertBefore);
        RestoreScrollAnchor?.Invoke(scrollAnchor);
    }

    /// <summary>
    /// In-place ADD twin of <see cref="MoveWithinLiveView"/> for a single new row: appends to the
    /// live list and re-reads the same DataGridCollectionView when the active view shows the item,
    /// instead of the full ApplyFilter rebuild whose ItemsSource swap snaps the grid scroll. Always
    /// bumps the cache version so every other (and future) view rebuilds from _allItems on access.
    /// </summary>
    private void AddToLiveView(MediaItem item)
    {
        _dataVersion++;
        if (_activeViewConfig?.BaseFilter is { } visible && visible(item) && !FilteredItems.Contains(item))
        {
            var scrollAnchor = GetScrollAnchor?.Invoke();
            FilteredItems.Add(item);
            FilteredItemsView?.Refresh();
            UpdateViewStats(_activeViewConfig, FilteredItems);
            RestoreScrollAnchor?.Invoke(scrollAnchor);
            NotifyLiveViewCountChanged();
        }
    }

    /// <summary>
    /// The in-place live-view mutations above change FilteredItems' COUNT without reassigning the
    /// property, so count-derived bindings never hear about it - the "No music on this device yet"
    /// line sat over the first synced tracks until a view swap rebuilt the list.
    /// </summary>
    private void NotifyLiveViewCountChanged()
    {
        OnPropertyChanged(nameof(ShowEmptyView));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    /// <summary>In-place REMOVE twin of <see cref="MoveWithinLiveView"/> - same reasoning, same
    /// scroll preservation, same cache-version bump.</summary>
    private void RemoveFromLiveView(MediaItem item)
    {
        _dataVersion++;
        var scrollAnchor = GetScrollAnchor?.Invoke();
        if (FilteredItems.Remove(item))
        {
            FilteredItemsView?.Refresh();
            if (_activeViewConfig != null)
            {
                UpdateViewStats(_activeViewConfig, FilteredItems);
            }
            RestoreScrollAnchor?.Invoke(scrollAnchor);
            NotifyLiveViewCountChanged();
        }
    }

    /// <summary>
    /// Reflects a row move in the LIVE view: mutates the current FilteredItems list in place and
    /// re-reads the same DataGridCollectionView. Never goes through ApplyFilter for a reorder - that
    /// swaps the grid's ItemsSource (new list + new view), which resets its scroll position to the
    /// top for a frame before the restore lands: the visible "jump".
    /// </summary>
    private void MoveWithinLiveView(MediaItem moved, MediaItem? target, bool insertBefore)
    {
        var list = FilteredItems;
        int fromIdx = list.IndexOf(moved);
        if (fromIdx < 0)
        {
            return;
        }
        list.RemoveAt(fromIdx);
        int insertIdx = target != null ? list.IndexOf(target) : list.Count;
        if (insertIdx < 0)
        {
            insertIdx = list.Count;
        }
        else if (target != null && !insertBefore)
        {
            insertIdx++;
        }
        list.Insert(Math.Clamp(insertIdx, 0, list.Count), moved);
        FilteredItemsView?.Refresh();
    }

    /// <summary>
    /// The connected device whose flat play order the current view shows, when that device's tier
    /// supports reordering (Shuffles - the iTunesSD list IS the play order). Null for every other view,
    /// including a device's kind sub-views. Used by the view to enable drag-to-reorder.
    /// </summary>
    internal ConnectedDevice? ActiveReorderableDevice
    {
        get
        {
            var key = _activeViewConfig?.Key;
            if (key == null)
            {
                return null;
            }
            var dev = _connectedDevices.Values.FirstOrDefault(d => string.Equals(key, $"Device:{d.MountPath}", StringComparison.OrdinalIgnoreCase));
            if (dev == null || dev.DeviceType != DeviceType.StockIPod)
            {
                return null;
            }
            return IPodDevice.For(dev).SupportsReorder ? dev : null;
        }
    }

    /// <summary>
    /// Moves one track within the device's flat play order and persists the new order to the device.
    /// Item-based on purpose: the device view shows _allItems insertion order (the scan order = the
    /// on-device order; it has no Sorter), so moving the item within _allItems IS the reorder. A null
    /// <paramref name="target"/> moves the track to the end of the device's order;
    /// <paramref name="insertBefore"/> places it before (true) or after (false) the target - matching
    /// the insertion line the drag showed.
    /// </summary>
    internal void ReorderDeviceTrack(MediaItem? moved, MediaItem? target, bool insertBefore = false)
    {
        var dev = ActiveReorderableDevice;
        if (dev == null || moved == null || ReferenceEquals(moved, target))
        {
            return;
        }

        var source = $"device:{dev.MountPath}";
        int fromAll = _allItems.IndexOf(moved);
        int toAll = target != null ? _allItems.IndexOf(target) : -1;
        if (fromAll < 0 || moved.Source != source || (target != null && toAll < 0))
        {
            return;
        }

        var scrollAnchor = GetScrollAnchor?.Invoke();
        _allItems.RemoveAt(fromAll);
        if (target == null)
        {
            int last = -1;
            for (int i = 0; i < _allItems.Count; i++)
            {
                if (_allItems[i].Source == source && _allItems[i].Kind == MediaKind.Music)
                {
                    last = i;
                }
            }
            _allItems.Insert(last + 1, moved);
        }
        else
        {
            int insertIdx = _allItems.IndexOf(target);
            if (!insertBefore)
            {
                insertIdx++;
            }
            _allItems.Insert(Math.Clamp(insertIdx, 0, _allItems.Count), moved);
        }
        MoveWithinLiveView(moved, target, insertBefore);
        RestoreScrollAnchor?.Invoke(scrollAnchor);

        var ordered = _allItems.Where(i => i.Source == source && i.Kind == MediaKind.Music).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await IPodDevice.For(dev).ReorderAsync(ordered);
                _log.Information("Reordered play order on {Device}: {Count} tracks", dev.MountPath, ordered.Count);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to reorder tracks on {Device}", dev.MountPath);
                UI(() => UpdateMainStatus($"Couldn't reorder on {dev.Name}: {ex.Message}"));
            }
        });
    }

    /// <summary>
    /// Reverse sync: copies a track OFF a connected device into the local library folder
    /// ({library}/{Artist}/{Album}/), imports it, and optionally favorites it or adds it to a
    /// playlist. FairPlay-protected files are refused (they only play on the buyer's authorized
    /// devices); soft duplicates (same title + artist already in the library), low-quality sources
    /// (&lt; 128 kbps), and extension/format mismatches warn before anything is copied.
    /// </summary>
    internal async Task SyncDeviceTrackToLibraryAsync(MediaItem deviceItem, bool addToFavorites, int? playlistId)
    {
        if (deviceItem.Source?.StartsWith("device:", StringComparison.Ordinal) != true || string.IsNullOrEmpty(deviceItem.FilePath) || !File.Exists(deviceItem.FilePath))
        {
            UpdateMainStatus($"Can't sync “{deviceItem.Title}” — the file isn't reachable on the device.");
            return;
        }
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            UpdateMainStatus("No library folder is configured.");
            return;
        }
        if (IsProtectedTrack(deviceItem.FilePath))
        {
            UpdateMainStatus($"“{deviceItem.Title}” is FairPlay-protected — it can't be synced to the library.");
            return;
        }

        var warnings = new List<string>();
        var dup = _allItems.FirstOrDefault(i => IsLocalLibraryFile(i) && i.Kind == MediaKind.Music
            && !string.IsNullOrWhiteSpace(i.Title) && string.Equals(i.Title, deviceItem.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Artist ?? string.Empty, deviceItem.Artist ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (dup != null)
        {
            warnings.Add($"Your library already has “{dup.Title}” by {(string.IsNullOrWhiteSpace(dup.Artist) ? "an unknown artist" : dup.Artist)}.");
        }
        if (deviceItem.AudioBitrate is > 0 and < 128)
        {
            warnings.Add($"This file is low quality ({deviceItem.AudioBitrate} kbps).");
        }
        if (deviceItem.FileNameMatchesHeaders == false)
        {
            warnings.Add("Its file extension doesn't match its actual audio format.");
        }
        if (warnings.Count > 0)
        {
            var confirm = new Views.ConfirmDialog("Sync to Library", string.Join("\n\n", warnings) + "\n\nSync anyway?", "Sync");
            if (!await confirm.ShowDialog<bool>(_window))
            {
                return;
            }
        }

        var artist = SanitizeFolderName(string.IsNullOrWhiteSpace(deviceItem.Artist) ? "Unknown Artist" : deviceItem.Artist!);
        var album = SanitizeFolderName(string.IsNullOrWhiteSpace(deviceItem.Album) ? "Unknown Album" : deviceItem.Album!);
        var destDir = Path.Combine(App.FolderPath, artist, album);
        // Name the library copy from its tags when we have them - on-device files carry iTunes-style
        // 4-caps names (RLED.m4a) that would litter the library as gibberish.
        var baseName = !string.IsNullOrWhiteSpace(deviceItem.Title)
            ? SanitizeFolderName(deviceItem.Track is { } trackNo && trackNo > 0 ? $"{trackNo:00} - {deviceItem.Title}" : deviceItem.Title!)
            : Path.GetFileNameWithoutExtension(deviceItem.FilePath);
        var ext = Path.GetExtension(deviceItem.FilePath);
        var dest = Path.Combine(destDir, baseName + ext);
        for (int n = 2; File.Exists(dest); n++)
        {
            dest = Path.Combine(destDir, $"{baseName} ({n}){ext}");
        }

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                File.Copy(deviceItem.FilePath, dest);
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Reverse sync copy failed: {Source} -> {Dest}", deviceItem.FilePath, dest);
            UpdateMainStatus($"Couldn't copy “{deviceItem.Title}” — {ex.Message}");
            return;
        }

        // Import directly (the folder watcher would eventually catch it, but the caller needs the
        // library item NOW for the Favorites/playlist step; LibraryContainsPath dedupes the echo).
        var item = FileScanner.CreateMediaItemFromPath(dest);
        if (item == null)
        {
            UpdateMainStatus($"Copied “{deviceItem.Title}” but couldn't import it.");
            return;
        }
        _allItems.Add(item);
        await AnalyzeAllFilesAsync([item]);
        ApplyFilter();
        UpdateTitle();
        UpdateData();

        if (addToFavorites)
        {
            AddToFavorites(item);
        }
        else if (playlistId is { } pid)
        {
            AddTrackToPlaylist(pid, item);
        }

        _log.Information("Reverse-synced “{Title}” from {Device} -> {Dest}", deviceItem.Title, deviceItem.Source, dest);
        UpdateMainStatus($"Synced “{item.Title ?? deviceItem.Title}” to {(addToFavorites ? "Favorites" : playlistId != null ? "the playlist" : "your library")}.");
    }

    /// <summary>FairPlay detection: the .m4p extension, or a 'drms' sample entry hiding inside an
    /// .m4a/.m4b container (the extension alone can't tell a purchased-protected file).</summary>
    internal static bool IsProtectedTrack(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".m4p", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var f = TagLib.File.Create(filePath);
                return f.Properties.Codecs?.OfType<TagLib.IAudioCodec>().Any(c => c.Description?.Contains("drms", StringComparison.OrdinalIgnoreCase) == true) == true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static string SanitizeFolderName(string s) => Helpers.SafeName.For(s, Helpers.SafeName.Style.ReplaceTrimTrailing);


    /// <summary>
    /// Builds the playlist/Favorites header (name, source, stats, up to four mosaic covers)
    /// for the given view, or clears it for any other view. Covers are decoded off the UI
    /// thread; a fast re-selection is guarded so a stale build can't overwrite a newer view.
    /// </summary>
    private async Task BuildPlaylistHeaderAsync(SidebarItem? item)
    {
        List<MediaItem> tracks;
        string name;
        string source;

        if (item?.PlaylistId is int playlistId)
        {
            tracks = GetPlaylistMediaItems(playlistId);
            name = item.Name;
            source = await Task.Run(() => MediaCache.GetPlaylistSource(playlistId));
        }
        else if (item?.IsFavorites == true)
        {
            tracks = FavoriteMusicFiles();
            name = item.Name;
            source = "Favorites";
        }
        else
        {
            CurrentPlaylistHeader = null;
            return;
        }

        var count = tracks.Count;
        var totalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration?.Ticks ?? 0));
        var totalSize = tracks.Sum(t => t.FileSize ?? 0);
        var summary = $"{count:N0} {(count == 1 ? "song" : "songs")} · {FormatPlaylistDuration(totalDuration)} · {FormatHelper.FormatFileSize(totalSize)}";

        // One tile per song - the first four tracks in playlist order, each showing its own album
        // art or a no-art placeholder (null) when it has none. Duplicates are intentional: two
        // songs from the same album give two identical tiles. Cells past the song count stay null,
        // so a short playlist pads with placeholders.
        var first4 = tracks.Take(4).ToList();
        var covers = await Task.Run(() =>
        {
            var loaded = new List<Bitmap?>(4);
            foreach (var t in first4)
            {
                Bitmap? bmp = null;
                if (t.HasAlbumArt == true && !string.IsNullOrEmpty(t.FilePath))
                {
                    try
                    {
                        var bytes = ArtworkSource.EmbeddedArt(t.FilePath!);
                        if (bytes is { Length: > 0 })
                        {
                            bmp = ArtworkSource.BitmapFromBytes(bytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(ex, "Playlist header cover load failed for {Path}", t.FilePath);
                    }
                }
                loaded.Add(bmp);   // null → the tile renders the no-art placeholder
            }
            return loaded;
        });

        // The user may have switched views while covers decoded - don't clobber the new view.
        if (SelectedSidebarItem != item)
        {
            return;
        }

        CurrentPlaylistHeader = new PlaylistHeaderInfo
        {
            Name = name,
            SourceLabel = source,
            Summary = summary,
            Cover1 = covers.ElementAtOrDefault(0),
            Cover2 = covers.ElementAtOrDefault(1),
            Cover3 = covers.ElementAtOrDefault(2),
            Cover4 = covers.ElementAtOrDefault(3),
        };
    }

    private static string FormatPlaylistDuration(TimeSpan d)
        => d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";

}
