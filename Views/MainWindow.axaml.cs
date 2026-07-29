// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using OrgZ.Controls;
using OrgZ.ViewModels;
using Optris.Icons.Avalonia;

namespace OrgZ.Views;

public partial class MainWindow : Window
{
    private static readonly Serilog.ILogger _log = Logging.For<MainWindow>();

    private readonly MainWindowViewModel _viewModel;

    /// <summary>Exposed for the docs-screenshot harness to seed view-model state.</summary>
    internal MainWindowViewModel ViewModel => _viewModel;

    private readonly Dictionary<string, EventHandler<RoutedEventArgs>> _menuHandlers;


    private string? _lastViewConfigKey;
    private readonly Dictionary<string, (string? AnchorId, MediaItem? SelectedItem)> _viewStates = new();

    internal static MediaItem? DraggedMediaItem;

    /// <summary>The full dragged selection, view-ordered. Single-row drags carry one
    /// item; sidebar drop targets iterate this instead of the anchor.</summary>
    internal static List<MediaItem> DraggedMediaItems = [];
    private static int _draggedPlaylistRowIndex = -1;
    private static readonly DataFormat<string> PlaylistRowDragFormat = DataFormat.CreateStringApplicationFormat("OrgZ.PlaylistRowIndex");
    private static readonly DataFormat<string> DeviceRowDragFormat = DataFormat.CreateStringApplicationFormat("OrgZ.DeviceRowIndex");
    private PointerPressedEventArgs? _gridPressEvent;
    private Point? _gridDragOrigin;
    private MediaItem? _gridDragItem;
    private int _gridDragRowIndex = -1;

    /// <summary>
    /// The selection as it stood when the mouse went down, captured because it won't
    /// survive long enough to read later: our press handler is on the TUNNEL route, so it
    /// runs before the DataGrid processes the same press and collapses the selection to
    /// the single row under the cursor. By the time a drag is recognised in PointerMoved,
    /// the live selection is one row - which is why dragging a multi-selection used to
    /// carry (and show) only one track.
    /// </summary>
    private List<MediaItem> _gridPressSelection = [];

    // Set by the docs-screenshot harness via the internal ctor: skips the live
    // library load and OS-service init in Loaded so the window renders with
    // seeded data only.
    private readonly bool _screenshotMode;

    public MainWindow() : this(false)
    {
    }

    internal MainWindow(bool screenshotMode)
    {
        _screenshotMode = screenshotMode;

        InitializeComponent();

        WindowSizeTracker.Track(this, "Main");

        MediaDataGrid.AddHandler(InputElement.PointerPressedEvent, MediaDataGrid_PointerPressed, RoutingStrategies.Tunnel);
        MediaDataGrid.AddHandler(InputElement.PointerMovedEvent, MediaDataGrid_PointerMoved, RoutingStrategies.Tunnel);
        MediaDataGrid.AddHandler(InputElement.PointerReleasedEvent, MediaDataGrid_PointerReleased, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(MediaDataGrid, true);
        MediaDataGrid.AddHandler(DragDrop.DragOverEvent, MediaDataGrid_DragOver);
        MediaDataGrid.AddHandler(DragDrop.DropEvent, MediaDataGrid_Drop);
        // Leaving the grid retires the insertion line but NOT the ghost - the pointer is
        // usually on its way to a sidebar playlist or device, where the ghost is the only
        // feedback there is.
        MediaDataGrid.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideDropLine());

        // The ghost follows the pointer anywhere in the window, so it's driven from the
        // window (tunnel, so a handled DragOver deeper in the tree can't stop it).
        AddHandler(DragDrop.DragOverEvent, (_, e) => UpdateDragGhost(e), RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, (_, _) => HideDragVisuals(), RoutingStrategies.Tunnel);

        // Column-header context menu: right-click any header → toggle columns + persist.
        // Wired at the tunneling stage so the DataGrid doesn't eat the event first.
        MediaDataGrid.AddHandler(InputElement.PointerPressedEvent, DataGrid_HeaderRightClick, RoutingStrategies.Tunnel);

        // Right-click SELECTS the row under the cursor before its context menu opens - Avalonia's
        // DataGrid only selects on the left button, so without this every context action (Add to
        // Playlist, Rating, Sync, ...) targeted whatever was last left-clicked, or nothing.
        MediaDataGrid.AddHandler(InputElement.PointerPressedEvent, DataGrid_RightClickSelectRow, RoutingStrategies.Tunnel);
        MediaDataGrid.ColumnReordered += DataGrid_ColumnReordered;

        // Every rebind of the grid's source is where grouped views get their collapse state, and
        // where per-header tap observers get attached.
        MediaDataGrid.LoadingRowGroup += AutoCollapseRowGroup;
        MediaDataGrid.PropertyChanged += (_, e) =>
        {
            if (e.Property == DataGrid.ItemsSourceProperty)
            {
                OnGridItemsSourceChanged();
            }
        };

        // Keyboard context-menu key (Apps / Shift+F10) opens the focused element's context menu by
        // re-dispatching ContextRequested - standard a11y, and it lets right-click menus be driven and
        // screenshotted without a mouse (e.g. the Avalonia devtools MCP, which can't synthesize a
        // right-click). Tunnel+bubble+handledEventsToo so it fires regardless of who's focused.
        AddHandler(InputElement.KeyDownEvent, OpenContextMenuOnKey, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        DataContext = _viewModel = new MainWindowViewModel(this, _screenshotMode);

        // Drive the VU repaint timer from the LCD page cycle: it should only
        // tick while the VU page is the active one, otherwise we waste CPU
        // analyzing audio whose meter isn't even visible.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentLcdPage))
            {
                // VU activation is owned by LcdDisplay itself.
            }
        };

        // The LCD itself stays at 588 by default but shrinks down to its
        // MinWidth (380) when the window can no longer fit the play controls,
        // album art, LCD, and search bar at full size. Driven from code-behind
        // because Avalonia 12's Grid crashes during measure when a *-sized
        // ColumnDefinition carries both MinWidth and MaxWidth.
        this.SizeChanged += (_, _) => UpdateLcdWidth();
        Loaded += (_, _) => UpdateLcdWidth();

        _menuHandlers = new Dictionary<string, EventHandler<RoutedEventArgs>>
        {
            ["Play"] = ContextMenu_Play,
            ["PlayNext"] = ContextMenu_PlayNext,
            ["AddToQueue"] = ContextMenu_AddToQueue,
            ["Favorite"] = ContextMenu_Favorite,
            ["GetInfo"] = ContextMenu_GetInfo,
            ["CopyUrl"] = ContextMenu_CopyUrl,
            ["Homepage"] = ContextMenu_Homepage,
            ["RemoveFromPlaylist"] = ContextMenu_RemoveFromPlaylist,
            ["ShowInExplorer"] = ContextMenu_ShowInExplorer,
            ["RemoveFromLibrary"] = ContextMenu_RemoveFromLibrary,
            ["RemoveFromDevice"] = ContextMenu_RemoveFromDevice,
            ["Check"] = (_, _) => _viewModel.SetChecked(SelectedTracks(), true),
            ["Uncheck"] = (_, _) => _viewModel.SetChecked(SelectedTracks(), false),
            ["RipTrack"] = async (s, e) => await _viewModel.RipSelectedCdTrackAsync(),
            ["RipCd"] = async (s, e) => await _viewModel.RipCurrentCdAsync(),
            ["BurnToCd"] = ContextMenu_BurnToCd,
        };

        _viewModel.PropertyChanging += ViewModel_PropertyChanging;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ScrollToSelectedRequested = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.SelectedItem == null)
                {
                    return;
                }

                // Going to the playing song OVERRIDES whatever scroll/selection this view had
                // remembered - otherwise the restore queued by the rebind lands a moment later
                // and yanks you back to where you last were, which is not where the song is.
                _pendingRestoreKey = null;
                MediaDataGrid.Opacity = 1;

                CenterOnItem(_viewModel.SelectedItem, allowRetry: true);
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        _viewModel.GetScrollAnchor = TopVisibleItem;
        _viewModel.RestoreScrollAnchor = anchor =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ScrollAnchorIntoView(anchor),
                Avalonia.Threading.DispatcherPriority.Background);
        };
        _viewModel.PlaylistsChanged = RebuildContextMenu;

        // Initialize UI state for the already-selected sidebar item
        _lastViewConfigKey = _viewModel.SelectedSidebarItem?.ViewConfigKey;
        var initialConfig = ListViewConfigs.Get(_lastViewConfigKey);
        if (initialConfig != null)
        {
            ApplyViewConfig(initialConfig);
        }

        Loaded += async (s, e) =>
        {
            // Screenshot harness seeds state directly; don't scan the real library
            // or spin up OS media-service integrations.
            if (_screenshotMode)
            {
                return;
            }

#if WINDOWS
            var handle = TryGetPlatformHandle();
            if (handle != null)
            {
                _viewModel.InitializeSmtc(handle.Handle);
                _viewModel.InitializeThumbBar(handle.Handle);
            }
#endif

            await _viewModel.LoadAsync();

            // Restore view state after items are loaded - use Background priority
            // so the DataGrid has time to measure and render rows first
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreViewState(_lastViewConfigKey), Avalonia.Threading.DispatcherPriority.Background);
            }, Avalonia.Threading.DispatcherPriority.Render);
        };
    }

    /// <summary>
    /// Saves the outgoing view's scroll offset and selection, BEFORE anything rebinds.
    ///
    /// This can't wait for PropertyChanged. The view model runs ApplyFilter from its
    /// OnSelectedSidebarItemChanged callback, and the toolkit calls that before raising
    /// PropertyChanged - so by the time the changed handler runs, the grid is already showing the
    /// INCOMING view and its scroll offset has been reset. Saving there recorded an offset of
    /// roughly zero under the outgoing view's key, every time, which is why no view remembered
    /// where it had been scrolled to.
    /// </summary>
    private void ViewModel_PropertyChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedSidebarItem))
        {
            SaveViewState();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedSidebarItem))
        {
            return;
        }

        // NOTE: the outgoing view's scroll/selection is saved from PropertyChanging, not here -
        // see ViewModel_PropertyChanging.

        var sidebarItem = _viewModel.SelectedSidebarItem;
        var config = ListViewConfigs.Get(sidebarItem?.ViewConfigKey);

        if (config != null)
        {
            ApplyViewConfig(config);
        }

        // The incoming view's state was already restored, synchronously, when its collection view
        // was bound - see OnGridItemsSourceChanged. Doing it from here would be a frame late and
        // visibly jump.
        _lastViewConfigKey = sidebarItem?.ViewConfigKey;
    }

    private DataGrid GetActiveDataGrid()
    {
        return MediaDataGrid;
    }

    /// <summary>
    /// The item in the topmost visible row - how a view's scroll position is remembered.
    ///
    /// NOT a pixel offset. Avalonia 12's DataGrid contains no <see cref="ScrollViewer"/> at all
    /// (it scrolls itself with a DataGridRowsPresenter and bare ScrollBars), so the offset this
    /// used to save was read off a null lookup: every save recorded zero and every restore did
    /// nothing. An anchor item also survives a re-sort, a filter change or a different row height,
    /// none of which a pixel offset does.
    /// </summary>
    private MediaItem? TopVisibleItem()
    {
        return MediaDataGrid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is MediaItem)
            .OrderBy(r => r.Bounds.Y)
            .Select(r => r.DataContext as MediaItem)
            .FirstOrDefault();
    }

    /// <summary>
    /// Scrolls so <paramref name="item"/> sits in the MIDDLE of the viewport - what "go to current
    /// song" should do, so what's coming next is visible alongside what's playing.
    ///
    /// Centring is just an anchor problem: put the row half a screen above it at the top.
    /// <see cref="DataGrid.ScrollIntoView"/> has no alignment argument, so there's no way to ask
    /// for this directly.
    /// </summary>
    private void CenterOnItem(MediaItem? item, bool allowRetry = false)
    {
        if (item is null)
        {
            return;
        }

        var items = _viewModel.FilteredItems;
        var index = items.FindIndex(i => i.Id == item.Id);
        if (index < 0)
        {
            return;
        }

        // Arriving from another view, the target's rows may not have realized yet, and a viewport
        // we measure as one row high would centre on nothing. Give it one more turn.
        var visibleRows = VisibleRowCount();
        if (visibleRows <= 1 && allowRetry)
        {
            Dispatcher.UIThread.Post(() => CenterOnItem(item), DispatcherPriority.Background);
            return;
        }

        ScrollAnchorIntoView(items[Math.Max(0, index - (visibleRows / 2))]);
    }

    private int VisibleRowCount()
    {
        return Math.Max(MediaDataGrid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible), 1);
    }

    /// <summary>
    /// Puts <paramref name="anchor"/> back at the top of the viewport, in ONE movement.
    ///
    /// <see cref="DataGrid.ScrollIntoView"/> docks its target to the nearest viewport edge. From
    /// the top of a list that edge is the bottom - so scrolling to the row that should END the
    /// viewport leaves the anchor exactly at its start. Scrolling to the anchor itself would park
    /// it at the bottom instead, a full screen out; and overshooting to the end of the list and
    /// coming back lands it correctly but moves the viewport twice, which reads as a jump to the
    /// bottom followed by a slam back.
    ///
    /// Callers must invoke this in the same dispatcher turn as the rebind, so no frame paints at
    /// the top first.
    /// </summary>
    private void ScrollAnchorIntoView(MediaItem? anchor)
    {
        if (anchor is null)
        {
            return;
        }

        // Match by Id, not reference: a rebuilt view hands back equal-but-new instances.
        var items = _viewModel.FilteredItems;
        var index = items.FindIndex(i => i.Id == anchor.Id);
        if (index < 0 || items.Count == 0)
        {
            return;
        }

        // No UpdateLayout() here. Callers run this AFTER a layout pass has completed, on a plain
        // dispatcher callback - forcing another one from inside layout is what crashed the grid.
        var visibleRows = MediaDataGrid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);
        var lastInViewport = Math.Min(index + Math.Max(visibleRows, 1) - 1, items.Count - 1);
        MediaDataGrid.ScrollIntoView(items[lastInViewport], null);
    }

    private void SaveViewState()
    {
        if (_lastViewConfigKey == null)
        {
            return;
        }

        var anchor = TopVisibleItem();
        var selectedId = _viewModel.SelectedItem?.Id;
        _viewStates[_lastViewConfigKey] = (anchor?.Id, _viewModel.SelectedItem);
        _log.Debug("View state save: {ViewKey} anchor={Anchor}", _lastViewConfigKey, anchor?.Id ?? "<none>");

        // Persist to settings
        Settings.Set($"OrgZ.View.{_lastViewConfigKey}.TopId", anchor?.Id ?? string.Empty);
        Settings.Set($"OrgZ.View.{_lastViewConfigKey}.SelectedId", selectedId ?? string.Empty);
        Settings.Save();
    }

    /// <summary>
    /// Restores the incoming view's scroll + selection on the next layout pass, with the grid
    /// hidden until it's in position.
    ///
    /// It CANNOT be done inline. Scrolling (or setting selection, which scrolls) from inside the
    /// ItemsSource change notification re-enters the DataGrid while it is still rebuilding its
    /// slot bookkeeping; on a grouped view that corrupts it outright and the next layout pass
    /// throws from deep inside <c>DataGridDisplayData</c>. That's a hard crash, and
    /// <c>MediaGridScrollRestoreTests</c> reproduces it.
    ///
    /// But deferring it is exactly what made the list paint at the top and then visibly jump. So
    /// the grid is made invisible for the one frame in between: by the time it's shown again it
    /// is already scrolled where the user left it.
    /// </summary>
    private void QueueViewStateRestore(string? key)
    {
        if (key is null || _pendingRestoreKey is not null)
        {
            return;
        }

        // Views with nothing to restore - never visited, or last left at the top - skip the whole
        // hide/defer dance. Paying a hidden frame and a deferred pass on EVERY switch is what made
        // switching feel heavy, and most switches have nothing to put back.
        if (!HasScrollToRestore(key))
        {
            return;
        }

        _pendingRestoreKey = key;
        MediaDataGrid.Opacity = 0;
        MediaDataGrid.LayoutUpdated += OnLayoutUpdatedForRestore;
    }

    /// <summary>Whether a view has a remembered position worth the cost of restoring it.</summary>
    private bool HasScrollToRestore(string key)
    {
        if (_viewStates.TryGetValue(key, out var state))
        {
            return !string.IsNullOrEmpty(state.AnchorId);
        }

        return !string.IsNullOrEmpty(Settings.Get($"OrgZ.View.{key}.TopId", string.Empty));
    }

    private void OnLayoutUpdatedForRestore(object? sender, EventArgs e)
    {
        MediaDataGrid.LayoutUpdated -= OnLayoutUpdatedForRestore;

        // Do NOT scroll from in here. LayoutUpdated fires from inside the layout pass, and
        // scrolling re-enters measure - which is the same re-entrancy that crashes the grid from
        // the ItemsSource notification. Hand off to the dispatcher so the pass finishes first.
        Dispatcher.UIThread.Post(RunPendingRestore, DispatcherPriority.Background);
    }

    private void RunPendingRestore()
    {
        var key = _pendingRestoreKey;
        _pendingRestoreKey = null;
        try
        {
            RestoreViewState(key);
        }
        catch (Exception ex)
        {
            // A failed restore is a cosmetic loss; it must never take the window with it.
            _log.Warning(ex, "View state restore failed for {ViewKey}", key);
        }
        finally
        {
            // Whatever happened, the grid must not be left invisible.
            MediaDataGrid.Opacity = 1;
        }
    }

    private string? _pendingRestoreKey;

    private void RestoreViewState(string? key)
    {
        if (key == null)
        {
            return;
        }

        // Try in-memory state first, then fall back to persisted settings
        if (_viewStates.TryGetValue(key, out var state))
        {
            if (state.SelectedItem != null && _viewModel.FilteredItems.Contains(state.SelectedItem))
            {
                _viewModel.SelectedItem = state.SelectedItem;
            }

            RestoreAnchorById(key, state.AnchorId);
            return;
        }

        // Restore from persisted settings (app restart)
        var savedTopId = Settings.Get($"OrgZ.View.{key}.TopId", string.Empty);
        var savedSelectedId = Settings.Get($"OrgZ.View.{key}.SelectedId", string.Empty);

        if (!string.IsNullOrEmpty(savedSelectedId))
        {
            var item = _viewModel.FilteredItems.FirstOrDefault(i => i.Id == savedSelectedId);
            if (item != null)
            {
                _viewModel.SelectedItem = item;
            }
        }

        RestoreAnchorById(key, savedTopId);
    }

    private void RestoreAnchorById(string key, string? anchorId)
    {
        if (string.IsNullOrEmpty(anchorId))
        {
            return;
        }

        var anchor = _viewModel.FilteredItems.FirstOrDefault(i => i.Id == anchorId);
        if (anchor is null)
        {
            // The row is gone - deleted, filtered out, or a device that unmounted. Top is right.
            _log.Debug("View state restore: {ViewKey} anchor {Anchor} no longer in view", key, anchorId);
            return;
        }

        ScrollAnchorIntoView(anchor);
    }

    // Click handler for the error badge in the inlined view footer (the
    // chunk of XAML that used to live in the StatusBar UserControl). Opens
    // the message log dialog - same behavior as the old
    // StatusBar.ErrorButtonClicked event, just wired directly now.
    private async void StatusBarErrorButton_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ShowMessageLog();
    }

    // -- Audio output flyout (mirrors MiniPlayerWindow's) --------------------

    private void AudioOutputButton_Click(object? sender, RoutedEventArgs e)
    {
        PopulateAudioOutputFlyout();
    }

    private void AudioOutputRefresh_Click(object? sender, RoutedEventArgs e)
    {
        PopulateAudioOutputFlyout();
    }

    private void PopulateAudioOutputFlyout()
    {
        OrgZ.Services.AudioOutput.AudioOutputFlyoutHelper.Populate(_viewModel._audioOutput, AudioOutputDeviceList);
    }

    /// <summary>
    /// Drives the LCD's width based on available horizontal space in the top
    /// controls row. Keeps the LCD at <c>588</c> by default, shrinks down to
    /// <c>MinWidth=380</c> when the window can't fit play + art + LCD + search
    /// at full size. Left + right cells stay anchored to their respective edges
    /// because cols 1 and 4 are <c>*</c> spacers that absorb any growth slack
    /// once the LCD reaches its 588 ceiling.
    /// </summary>
    private void UpdateLcdWidth()
    {
        if (TopControlsGrid == null || MainLcdPanel == null) return;
        var gridWidth = TopControlsGrid.Bounds.Width;
        if (gridWidth <= 0) return;

        Control? play = null, art = null, search = null;
        foreach (var child in TopControlsGrid.Children)
        {
            if (child is not Control c) continue;
            var col = Grid.GetColumn(c);
            if (col == 0) play = c;
            else if (col == 2) art = c;
            else if (col == 5) search = c;
        }

        static double SumWithMargin(Control? ctrl)
        {
            if (ctrl == null) return 0;
            return ctrl.Bounds.Width + ctrl.Margin.Left + ctrl.Margin.Right;
        }

        var otherCols = SumWithMargin(play) + SumWithMargin(art) + SumWithMargin(search);
        var lcdHostMargin = MainLcdPanel.Parent is Control parent
            ? parent.Margin.Left + parent.Margin.Right
            : 8;

        var available = gridWidth - otherCols - lcdHostMargin;
        var target = Math.Clamp(available, 380, 588);

        // Skip the assignment when nothing meaningfully changed - Width writes
        // re-trigger a layout pass, and SizeChanged fires within that pass, so
        // any oscillation here turns into a layout loop.
        if (Math.Abs(MainLcdPanel.Width - target) > 0.5)
        {
            MainLcdPanel.Width = target;
        }
    }

    private void ApplyViewConfig(ListViewConfig config)
    {
        // The config names its host directly (ViewHost), so the grid/panel routing keys off one
        // discriminator instead of being re-derived from view-specific flags at each consumer.
        bool isPodcasts = config.Host == ViewHost.PodcastsPanel;
        bool isAudiobooks = config.Host == ViewHost.AudiobooksPanel;

        // Podcasts (the store) uses its own UserControl instead of a DataGrid. Hide the grid and show
        // the panel; the panel's internal nav switches between store / subscriptions / feed-detail
        // without touching the DataGrid pipeline.
        PodcastsPanel.IsVisible = isPodcasts;
        AudiobooksHost.IsVisible = isAudiobooks;
        MediaDataGrid.IsVisible = config.Host == ViewHost.Grid;

        // Set RadioFilterPanel visibility before any early-return so a previous
        // Radio view doesn't leave its country/genre dropdowns hanging in the
        // Podcasts view.
        RadioFilterPanel.IsVisible = config.ShowRadioFilterPanel;

        if (isPodcasts)
        {
            _ = _viewModel.Podcasts.LoadStoreAsync();
            return;
        }

        if (isAudiobooks)
        {
            // The store IS the page (like Podcasts); your owned books show as a section on it.
            _viewModel.Audiobooks.RefreshOwned();
            _ = _viewModel.Audiobooks.LoadStoreAsync();
            return;
        }

        // One grid, so its columns and context menu are rebuilt on every switch rather than
        // built once per host. MediaGrid.ReplaceColumns is what makes that safe under grouping.
        BuildColumns(config.Columns);
        BuildContextMenu(config.ContextMenuItems);

        // Collapse state is applied where the source is bound (OnGridItemsSourceChanged), not
        // here: the view model assigns FilteredItemsView from its own property-changed callback,
        // which runs BEFORE this handler, and it also reassigns on search/filter changes that
        // never reach ApplyViewConfig at all. Keying off the bind covers both, in order.
        EnsureGroupExpansionFor(config);
    }

    /// <summary>
    /// Loads the persisted expand/collapse dictionary for a view, if it isn't already loaded.
    ///
    /// Keyed on the config being applied - NOT <see cref="_lastViewConfigKey"/>, which at
    /// ApplyViewConfig time is still the view being LEFT. Getting that wrong made the radio
    /// groups load and save their state under whichever view you arrived from.
    /// </summary>
    private void EnsureGroupExpansionFor(ListViewConfig? config)
    {
        var key = config?.GroupByPath is null ? null : config.Key;
        if (key == _currentGroupedViewKey)
        {
            return;
        }

        _currentGroupedViewKey = key;
        _appliedExpansionKeys.Clear();
        _groupExpansion = string.IsNullOrEmpty(key)
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : GroupExpansionState.Load(key!);
    }

    /// <summary>
    /// Applies saved collapse state the moment a grouped collection view is bound, in the same
    /// dispatcher turn - so a grouped view never paints expanded and then snap shut.
    /// </summary>
    private void OnGridItemsSourceChanged()
    {
        var incomingKey = _viewModel.SelectedSidebarItem?.ViewConfigKey;
        EnsureGroupExpansionFor(ListViewConfigs.Get(incomingKey));
        QueueViewStateRestore(incomingKey);

        if (_currentGroupedViewKey is null)
        {
            return;
        }

        _applyingGroupExpansion = true;
        try
        {
            MediaGrid.ApplyGroupExpansion(MediaDataGrid, MediaDataGrid.ItemsSource as DataGridCollectionView, key =>
            {
                _appliedExpansionKeys.Add(key);

                // Unseen groups default to collapsed, and the choice is recorded so it persists.
                if (!_groupExpansion.TryGetValue(key, out var expanded))
                {
                    _groupExpansion[key] = false;
                    return false;
                }

                return expanded;
            });
        }
        finally
        {
            _applyingGroupExpansion = false;
        }

        PersistGroupExpansion();
    }

    /// <summary>
    /// Per-group expansion state for the currently active grouped view. Loaded from
    /// Settings when entering a grouped view, updated on every toggle, persisted on
    /// every change. True = expanded, false = collapsed, missing = never seen
    /// (treated as collapsed and recorded as such on first materialization).
    /// </summary>
    private Dictionary<string, bool> _groupExpansion = new(StringComparer.Ordinal);
    private string? _currentGroupedViewKey;

    /// <summary>
    /// Headers already wired to <see cref="OnGroupHeaderStateChanged"/>. The DataGrid recycles a
    /// small, viewport-sized pool of these and hands the same instances back for different groups,
    /// so this stays bounded and the observer reads the group off the header at fire time.
    /// </summary>
    private readonly HashSet<DataGridRowGroupHeader> _observedHeaders = new();

    /// <summary>
    /// Group keys we've already applied the saved expansion state to during this view
    /// session. LoadingRowGroup fires every time a header re-enters the viewport -
    /// without this guard we'd re-apply the saved state on every scroll-in, and a
    /// programmatic Expand snaps the viewport to the expanded group (the cause of the
    /// "jumps up + opens first genre" bug when the user collapses a group further down).
    /// Reset on every view switch in <see cref="ApplyViewConfig"/>.
    /// </summary>
    private readonly HashSet<string> _appliedExpansionKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Collapses every group in the active grouped view and persists it - backs the
    /// "collapse all" button on the radio filter bar. No-op outside a grouped view.
    /// </summary>
    internal void CollapseAllRowGroups()
    {
        if (MediaDataGrid.ItemsSource is not DataGridCollectionView view || view.Groups is null)
        {
            return;
        }

        _applyingGroupExpansion = true;
        try
        {
            MediaGrid.ApplyGroupExpansion(MediaDataGrid, view, key =>
            {
                _groupExpansion[key] = false;
                return false;
            });
        }
        finally
        {
            _applyingGroupExpansion = false;
        }

        PersistGroupExpansion();
    }

    private void AutoCollapseRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        // No per-header collapse is applied here - doing it as each header realized is what made
        // the grid collapse group-by-group "over time". The saved state is applied in one batch at
        // bind time by OnGridItemsSourceChanged (which also covers groups that haven't realized
        // yet). All that remains is watching each header for changes to its real expanded state.
        var header = e.RowGroupHeader;
        if (_observedHeaders.Add(header))
        {
            header.Classes.CollectionChanged += (s, _) => OnGroupHeaderStateChanged(s as Classes, header);
        }
    }

    /// <summary>
    /// Records a group's expanded state from what the header ACTUALLY is, rather than from a tap.
    ///
    /// The previous version listened for <c>Tapped</c> and flipped the stored boolean. That
    /// desynced constantly, because Avalonia's row-group header does not toggle on a single click
    /// - only on the expander button or a double click (see
    /// <c>DataGridRowGroupHeader.DataGridRowGroupHeader_PointerPressed</c>). Any other click on the
    /// header flipped our dictionary while the visual stayed put, and from then on the state was
    /// inverted: one click appeared to do nothing, and returning to the view re-applied the wrong
    /// value and re-opened a group you had closed.
    ///
    /// The header exposes the truth as an <c>:expanded</c> pseudo-class, which lands in
    /// <see cref="StyledElement.Classes"/>, so this reads it instead of predicting it.
    /// </summary>
    private void OnGroupHeaderStateChanged(Classes? classes, DataGridRowGroupHeader header)
    {
        // Programmatic expand/collapse (applying saved state, "collapse all") moves these classes
        // too. Those changes originate FROM the dictionary, so writing them back is at best a
        // no-op, and during a rebind the header may still be showing its previous group.
        if (_applyingGroupExpansion
            || classes is null
            || _currentGroupedViewKey is null
            || header.DataContext is not DataGridCollectionViewGroup group)
        {
            return;
        }

        var key = group.Key?.ToString() ?? string.Empty;
        var expanded = classes.Contains(":expanded");
        if (_groupExpansion.TryGetValue(key, out var previous) && previous == expanded)
        {
            return;
        }

        _groupExpansion[key] = expanded;
        PersistGroupExpansion();
    }

    /// <summary>
    /// Set while collapse state is being pushed INTO the grid, so the header observers above don't
    /// treat our own writes as user intent.
    /// </summary>
    private bool _applyingGroupExpansion;

    private void PersistGroupExpansion()
    {
        if (!string.IsNullOrEmpty(_currentGroupedViewKey))
        {
            GroupExpansionState.Save(_currentGroupedViewKey!, _groupExpansion);
        }
    }

    private void BuildColumns(List<ColumnDef> columnDefs)
    {
        BuildColumnsOn(MediaDataGrid, columnDefs);
    }

    /// <summary>
    /// Returns the ordered column defs for a view, honoring any saved user order. Columns
    /// the user has in their saved state come first in saved order; columns added to the
    /// config after the state was saved append at the end so they're never silently lost.
    /// </summary>
    private static List<ColumnDef> ApplySavedOrder(string? viewKey, List<ColumnDef> defs)
    {
        if (string.IsNullOrEmpty(viewKey))
        {
            return defs;
        }

        var savedKeys = ColumnStateStore.LoadOrder(viewKey);
        if (savedKeys.Count == 0)
        {
            return defs;
        }

        var byKey = defs.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var ordered = new List<ColumnDef>(defs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in savedKeys)
        {
            if (byKey.TryGetValue(key, out var def) && seen.Add(key))
            {
                ordered.Add(def);
            }
        }
        // Append any columns introduced since the state was saved
        foreach (var def in defs)
        {
            if (seen.Add(def.Key))
            {
                ordered.Add(def);
            }
        }
        return ordered;
    }

    private void BuildColumnsOn(DataGrid grid, List<ColumnDef> columnDefs)
    {
        var viewKey = _lastViewConfigKey ?? _viewModel.SelectedSidebarItem?.ViewConfigKey;
        columnDefs = ApplySavedOrder(viewKey, columnDefs);

        // Built into a list first, then swapped in one go by MediaGrid.ReplaceColumns - which is
        // what makes a rebuild survive a bound grouped view. A throw while building a column
        // therefore leaves the previous columns intact rather than a half-emptied grid.
        var built = new List<DataGridColumn>(columnDefs.Count);

        foreach (var def in columnDefs)
        {
            DataGridColumn col = def.Type switch
            {
                ColumnType.CheckBox => new DataGridCheckBoxColumn
                {
                    Header = def.Header,
                    Binding = new Binding(def.BindingPath),
                },
                // The iTunes tick: centred in its narrow column and scaled down - the
                // stock DataGridCheckBoxColumn renders a full-size box hard against the
                // left edge, which reads as a form control rather than a row marker.
                ColumnType.RowCheck => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((_, _) => new CheckBox
                    {
                        [!Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty] = new Binding(def.BindingPath) { Mode = BindingMode.TwoWay },
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(0),
                        MinWidth = 0,
                        RenderTransform = new ScaleTransform(0.75, 0.75),
                        RenderTransformOrigin = RelativePoint.Center,
                    }),
                },
                ColumnType.PlayIndicator => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    // Shows play (priority) / rip status (pending · ripping · done).
                    CellTemplate = new FuncDataTemplate<MediaItem>((_, _) => new RipStatusIndicator()),
                },
                ColumnType.FavoriteTitle => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var starEmpty = new Icon
                        {
                            Value = "fa-regular fa-star",
                            FontSize = 12,
                            Opacity = 0.15,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        starEmpty.Bind(IsVisibleProperty, new Binding("!IsFavorite"));

                        var starFilled = new Icon
                        {
                            Value = "fa-solid fa-star",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Colors.Gold),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        starFilled.Bind(IsVisibleProperty, new Binding("IsFavorite"));

                        var starPanel = new Panel
                        {
                            Width = 14,
                            Height = 14,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { starEmpty, starFilled },
                        };

                        var titleBlock = new TextBlock
                        {
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        titleBlock.Bind(TextBlock.TextProperty, new Binding("Title"));

                        var stack = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center,
                            Spacing = 6,
                            Margin = new Thickness(4, 0),
                            Children = { starPanel, titleBlock },
                        };
                        return stack;
                    }),
                },
                ColumnType.Centered => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var tb = new TextBlock
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        ApplyColumnTextOverrides(tb, def);
                        tb.Bind(TextBlock.TextProperty, new Binding(def.BindingPath) { StringFormat = def.StringFormat });
                        return tb;
                    }),
                },
                ColumnType.RightAligned => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var tb = new TextBlock
                        {
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(8, 0),
                        };
                        ApplyColumnTextOverrides(tb, def);
                        tb.Bind(TextBlock.TextProperty, new Binding(def.BindingPath) { StringFormat = def.StringFormat });
                        return tb;
                    }),
                },
                ColumnType.Badge => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var tb = new TextBlock
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontWeight = FontWeight.SemiBold,
                            FontSize = 10,
                        };
                        ApplyColumnTextOverrides(tb, def);
                        tb.Bind(TextBlock.TextProperty, new Binding(def.BindingPath) { StringFormat = def.StringFormat });
                        return tb;
                    }),
                },
                ColumnType.Flag => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var img = new Image
                        {
                            Width = 24,
                            Height = 16,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        img.Bind(Image.SourceProperty,
                            new Binding(def.BindingPath)
                            {
                                Converter = Converters.CountryCodeToFlagConverter.Instance,
                            });
                        img.Bind(ToolTip.TipProperty, new Binding(nameof(MediaItem.CountryTooltip)));
                        return img;
                    }),
                },
                ColumnType.Rating => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        // Five slots: a faint outline star always shows (75% transparent - the
                        // "no rating here" look), with a solid star layered on top and toggled
                        // per-slot by the rating, so re-rating updates live via the binding.
                        var row = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 1,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };

                        for (int position = 1; position <= 5; position++)
                        {
                            var outline = new Icon
                            {
                                Value = "fa-regular fa-star",
                                FontSize = 12,
                                Opacity = 0.25,
                                VerticalAlignment = VerticalAlignment.Center,
                                HorizontalAlignment = HorizontalAlignment.Center,
                            };
                            var filled = new Icon
                            {
                                Value = "fa-solid fa-star",
                                FontSize = 12,
                                VerticalAlignment = VerticalAlignment.Center,
                                HorizontalAlignment = HorizontalAlignment.Center,
                            };
                            filled.Bind(IsVisibleProperty, new Binding(nameof(MediaItem.Rating))
                            {
                                Converter = Converters.RatingStarConverter.Instance,
                                ConverterParameter = position,
                            });

                            row.Children.Add(new Panel { Width = 15, Height = 14, Children = { outline, filled } });
                        }
                        return row;
                    }),
                },
                _ => new DataGridTextColumn
                {
                    Header = def.Header,
                    Binding = new Binding(def.BindingPath) { StringFormat = def.StringFormat },
                },
            };

            col.Width = new DataGridLength(def.WidthValue, def.WidthType);
            col.CanUserSort = def.CanUserSort;
            col.CanUserResize = def.CanUserResize;
            col.CanUserReorder = def.CanUserReorder;

            // Apply saved visibility override if one exists, else use the column def's
            // IsDefaultVisible. The "#" track column and "Title" are always kept visible
            // even if somehow saved-off - losing either would strand the user.
            bool? savedVisible = string.IsNullOrEmpty(viewKey) ? null : ColumnStateStore.GetVisibility(viewKey!, def.Key);
            col.IsVisible = savedVisible ?? def.IsDefaultVisible;

            // Tag the column with its Key so the reorder + toggle handlers can look it
            // up by DataGridColumn without having to maintain a parallel map.
            col.SetValue(ColumnKeyProperty, def.Key);

            built.Add(col);
        }

        MediaGrid.ReplaceColumns(grid, built);
    }

    /// <summary>
    /// Applies per-column text overrides (FontSize, LetterSpacing) to a cell TextBlock
    /// when the ColumnDef opts in. Used by Centered / RightAligned templates so narrow
    /// numeric columns (like the "#" track column) can tighten their text without
    /// affecting the rest of the grid.
    /// </summary>
    private static void ApplyColumnTextOverrides(TextBlock tb, ColumnDef def)
    {
        if (def.FontSize.HasValue)
        {
            tb.FontSize = def.FontSize.Value;
        }
        if (def.LetterSpacing.HasValue)
        {
            tb.LetterSpacing = def.LetterSpacing.Value;
        }
    }

    /// <summary>
    /// Attached property that tags a DataGridColumn with its stable ColumnDef.Key,
    /// used to look up the column in saved state when the user toggles or reorders.
    /// </summary>
    internal static readonly AttachedProperty<string?> ColumnKeyProperty =
        AvaloniaProperty.RegisterAttached<MainWindow, DataGridColumn, string?>("ColumnKey");

    /// <summary>
    /// Opens the focused element's context menu when the keyboard context-menu key is pressed
    /// (Apps, or Shift+F10) by re-dispatching a synthetic <see cref="ContextRequestedEventArgs"/> -
    /// the same event a right-click raises. Makes every right-click menu keyboard-accessible and
    /// driveable from automation tooling that can't synthesize a right-click.
    /// </summary>
    private void OpenContextMenuOnKey(object? sender, KeyEventArgs e)
    {
        bool isMenuKey = e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (!isMenuKey)
        {
            return;
        }
        // Use the key event's target (the focused element for a real keypress; the element an
        // automation tool dispatched the key to) - NOT FocusManager, which doesn't reflect a
        // tool-injected focus.
        var target = (e.Source as InputElement) ?? FocusManager?.GetFocusedElement() as InputElement;
        if (target is not null)
        {
            // The hit-test handlers resolve the menu via e.Source + FindAncestorOfType, which EXCLUDES
            // self - so raise from a visual descendant (header content) of the target, not the target
            // itself, or the lookup returns null and no menu is built.
            var source = target.GetVisualDescendants().OfType<InputElement>().FirstOrDefault() ?? target;
            source.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = InputElement.ContextRequestedEvent, Source = source });
            e.Handled = true;
        }
    }

    private void DataGrid_RightClickSelectRow(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            return;
        }
        // A row, not a header (the header right-click owns the column menu). Select the item under
        // the cursor so the context menu that's about to open acts on THIS row.
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>()?.DataContext is MediaItem item)
        {
            _viewModel.SelectedItem = item;
        }
    }

    private void DataGrid_HeaderRightClick(object? sender, PointerPressedEventArgs e)
    {
        // Only react to right-click (secondary mouse button) on a DataGridColumnHeader.
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            return;
        }

        var header = (e.Source as Visual)?.FindAncestorOfType<Avalonia.Controls.DataGridColumnHeader>();
        if (header == null || sender is not DataGrid grid)
        {
            return;
        }

        var viewKey = _lastViewConfigKey ?? _viewModel.SelectedSidebarItem?.ViewConfigKey;
        var config = ListViewConfigs.Get(viewKey);
        if (config == null)
        {
            return;
        }

        var menu = new Avalonia.Controls.ContextMenu();
        foreach (var def in config.Columns)
        {
            // Skip the play-indicator / no-header columns - they're structural, can't
            // meaningfully be toggled by users.
            if (string.IsNullOrEmpty(def.Header))
            {
                continue;
            }

            var col = grid.Columns.FirstOrDefault(c => c.GetValue(ColumnKeyProperty) == def.Key);
            bool isVisible = col?.IsVisible ?? def.IsDefaultVisible;

            var item = new Avalonia.Controls.MenuItem
            {
                Header = def.Header,
                Icon = isVisible
                    ? new Icon { Value = "fa-solid fa-check", FontSize = 12 }
                    : null,
            };
            var capturedKey = def.Key;
            var capturedVisible = isVisible;
            item.Click += (_, _) => ToggleColumn(grid, viewKey!, capturedKey, !capturedVisible);
            menu.Items.Add(item);
        }

        header.ContextMenu = menu;
        menu.Open(header);
        e.Handled = true;
    }

    private void ToggleColumn(DataGrid grid, string viewKey, string columnKey, bool newVisible)
    {
        var col = grid.Columns.FirstOrDefault(c => c.GetValue(ColumnKeyProperty) == columnKey);
        if (col != null)
        {
            col.IsVisible = newVisible;
        }

        // Persist the full state so saved visibility survives a view rebuild
        PersistColumnState(grid, viewKey);
    }

    private void DataGrid_ColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }
        var viewKey = _lastViewConfigKey ?? _viewModel.SelectedSidebarItem?.ViewConfigKey;
        if (string.IsNullOrEmpty(viewKey))
        {
            return;
        }
        PersistColumnState(grid, viewKey);
    }

    /// <summary>
    /// Walks the grid's columns in display-index order and writes the (key, visibility)
    /// pairs to ColumnStateStore. Skips columns without a Key (structural extras).
    /// </summary>
    private static void PersistColumnState(DataGrid grid, string viewKey)
    {
        var ordered = grid.Columns
            .Where(c => !string.IsNullOrEmpty(c.GetValue(ColumnKeyProperty)))
            .OrderBy(c => c.DisplayIndex)
            .Select(c => new ColumnStateStore.ColumnState
            {
                Key = c.GetValue(ColumnKeyProperty)!,
                IsVisible = c.IsVisible,
            })
            .ToList();

        ColumnStateStore.Save(viewKey, ordered);
    }

    private void BuildContextMenu(List<ContextMenuItemDef> defs)
    {
        BuildContextMenuOn(MediaDataGrid, defs);
    }

    // Commands whose menu entry gets a live "(N)" count suffix when several rows are
    // selected - every verb that operates on the whole selection.
    private static readonly HashSet<string> _selectionAwareCommands =
    [
        "PlayNext", "AddToQueue", "Favorite", "RemoveFromPlaylist",
        "RemoveFromLibrary", "RemoveFromDevice", "RestoreFromIgnored", "BurnToCd",
    ];

    private void BuildContextMenuOn(DataGrid grid, List<ContextMenuItemDef> defs)
    {
        var menu = new ContextMenu();
        BuildMenuItems(menu.Items, defs);

        // Selection-aware entries show the count on open: "Add to Queue (7)".
        menu.Opened += (_, _) =>
        {
            var count = grid.SelectedItems?.OfType<MediaItem>().Count() ?? 0;
            foreach (var obj in menu.Items)
            {
                if (obj is Avalonia.Controls.MenuItem { Tag: ContextMenuItemDef def } mi
                    && def.CommandName != null && _selectionAwareCommands.Contains(def.CommandName))
                {
                    mi.Header = count > 1 ? $"{def.Header} ({count})" : def.Header;
                }
            }
        };

        grid.ContextMenu = menu;
    }

    private void BuildMenuItems(Avalonia.Controls.ItemCollection items, List<ContextMenuItemDef> defs)
    {
        foreach (var def in defs)
        {
            if (def.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            var menuItem = new Avalonia.Controls.MenuItem
            {
                IsEnabled = def.IsEnabled,
                Header = def.Header,
                Tag = def,   // lets the menu's Opened handler re-title selection-aware entries
            };

            if (def.IsHeader)
            {
                // A label, not an action: non-interactive AND visibly dimmed so it reads as the
                // track it names rather than a clickable command sitting at full opacity.
                menuItem.IsHitTestVisible = false;
                menuItem.IsEnabled = false;
                menuItem.FontWeight = FontWeight.Bold;

                // Bind header text dynamically for title/artist display
                if (def.Header.StartsWith("{SelectedItem."))
                {
                    var path = def.Header.TrimStart('{').TrimEnd('}');
                    menuItem.Bind(Avalonia.Controls.MenuItem.HeaderProperty, new Binding(path) { FallbackValue = "(Unknown)" });
                }
            }

            if (def.CommandName != null && _menuHandlers.TryGetValue(def.CommandName, out var handler))
            {
                menuItem.Click += handler;
            }

            if (def.IsAddToPlaylistMarker)
            {
                // Rebuild on open - playlists load async after startup, but this menu is built once.
                menuItem.SubmenuOpened += (_, _) => PopulateAddToPlaylistMenu(menuItem);
                PopulateAddToPlaylistMenu(menuItem);
            }
            else if (def.IsRatingMarker)
            {
                PopulateRatingMenu(menuItem);
            }
            else if (def.IsSyncToDeviceMarker)
            {
                // Devices hot-plug, so rebuild the list every time the submenu opens rather than
                // at menu-build time (the grid's context menu is built once and reused).
                menuItem.SubmenuOpened += (_, _) => PopulateSyncToDeviceMenu(menuItem);
                PopulateSyncToDeviceMenu(menuItem);   // seed it so the arrow/placeholder shows immediately
            }
            else if (def.IsSyncToLibraryMarker)
            {
                // Playlists load async and change at runtime - rebuild on every open.
                menuItem.SubmenuOpened += (_, _) => PopulateSyncToLibraryMenu(menuItem);
                PopulateSyncToLibraryMenu(menuItem);
            }
            else if (def.Children is { Count: > 0 })
            {
                BuildMenuItems(menuItem.Items, def.Children);
            }

            items.Add(menuItem);
        }
    }

    private void PopulateRatingMenu(Avalonia.Controls.MenuItem parent)
    {
        // No Rating
        var noRating = new Avalonia.Controls.MenuItem { Header = "No Rating" };
        noRating.Click += (_, _) =>
        {
            if (_viewModel.SelectedItem != null)
            {
                _viewModel.SetRating(_viewModel.SelectedItem, null);
            }
        };
        parent.Items.Add(noRating);

        parent.Items.Add(new Separator());

        for (int i = 1; i <= 5; i++)
        {
            var stars = i;
            var label = new string('★', stars) + new string('☆', 5 - stars);
            var item = new Avalonia.Controls.MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                if (_viewModel.SelectedItem != null)
                {
                    _viewModel.SetRating(_viewModel.SelectedItem, stars);
                }
            };
            parent.Items.Add(item);
        }
    }

    private void PopulateSyncToDeviceMenu(Avalonia.Controls.MenuItem parent)
    {
        parent.Items.Clear();

        var item = _viewModel.SelectedItem;
        var targets = item is null
            ? []
            : _viewModel.ConnectedDevicesSnapshot().Where(d => _viewModel.CanSyncItemToDevice(item, d)).ToList();

        if (targets.Count == 0)
        {
            parent.Items.Add(new Avalonia.Controls.MenuItem { Header = "(No compatible devices)", IsEnabled = false });
            return;
        }

        foreach (var device in targets)
        {
            var dev = device;   // capture
            // Grey out a device the track is already on - the same "already there" cue the
            // Add-to-Playlist submenu gives for the current playlist.
            var entry = new Avalonia.Controls.MenuItem { Header = dev.SidebarLabel, IsEnabled = !_viewModel.IsItemAlreadyOnDevice(item, dev) };
            entry.Click += async (_, _) =>
            {
                if (_viewModel.SelectedItem is { } sel)
                {
                    await _viewModel.SyncItemToDeviceAsync(sel, dev);
                }
            };
            parent.Items.Add(entry);
        }
    }

    private void PopulateSyncToLibraryMenu(Avalonia.Controls.MenuItem parent)
    {
        parent.Items.Clear();

        var music = new Avalonia.Controls.MenuItem { Header = "Music" };
        music.Click += async (_, _) =>
        {
            if (_viewModel.SelectedItem is { } sel)
            {
                await _viewModel.SyncDeviceTrackToLibraryAsync(sel, addToFavorites: false, playlistId: null);
            }
        };
        parent.Items.Add(music);

        var favorites = new Avalonia.Controls.MenuItem { Header = "Favorites" };
        favorites.Click += async (_, _) =>
        {
            if (_viewModel.SelectedItem is { } sel)
            {
                await _viewModel.SyncDeviceTrackToLibraryAsync(sel, addToFavorites: true, playlistId: null);
            }
        };
        parent.Items.Add(favorites);

        var playlists = _viewModel.PlaylistItems.Where(p => p.PlaylistId.HasValue).ToList();
        if (playlists.Count > 0)
        {
            parent.Items.Add(new Separator());
            foreach (var p in playlists)
            {
                var playlistId = p.PlaylistId!.Value;
                var entry = new Avalonia.Controls.MenuItem { Header = p.Name };
                entry.Click += async (_, _) =>
                {
                    if (_viewModel.SelectedItem is { } sel)
                    {
                        await _viewModel.SyncDeviceTrackToLibraryAsync(sel, addToFavorites: false, playlistId: playlistId);
                    }
                };
                parent.Items.Add(entry);
            }
        }
    }

    private void PopulateAddToPlaylistMenu(Avalonia.Controls.MenuItem parent)
    {
        // Rebuild every time: playlists load asynchronously after startup, and the grid's context
        // menu is built once and reused - populating only at build time is why this listed nothing.
        parent.Items.Clear();

        // The playlist/Favorites you're currently VIEWING is greyed out - a track there is already
        // in it, so "add" is a no-op. (SelectedSidebarItem is the active view.)
        var current = _viewModel.SelectedSidebarItem;

        // Favorites is a pseudo-playlist and lives at the top; adding here never un-favorites.
        var favorites = new Avalonia.Controls.MenuItem { Header = "Favorites", IsEnabled = current?.IsFavorites != true };
        favorites.Click += (_, _) =>
        {
            foreach (var track in SelectedTracks())
            {
                _viewModel.AddToFavorites(track);
            }
        };
        parent.Items.Add(favorites);

        var playlists = _viewModel.PlaylistItems.Where(p => p.PlaylistId.HasValue).ToList();
        if (playlists.Count > 0)
        {
            parent.Items.Add(new Separator());
            foreach (var p in playlists)
            {
                var playlistId = p.PlaylistId!.Value;
                var item = new Avalonia.Controls.MenuItem { Header = p.Name, IsEnabled = current?.PlaylistId != playlistId };
                item.Click += (_, _) => _viewModel.AddTracksToPlaylist(playlistId, SelectedTracks());
                parent.Items.Add(item);
            }
        }
    }

    private void RebuildContextMenu()
    {
        var config = ListViewConfigs.Get(_lastViewConfigKey);
        if (config != null)
        {
            BuildContextMenuOn(MediaDataGrid, config.ContextMenuItems);
        }
    }

    private void DataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        _viewModel.DataGridRowDoubleClick();
    }

    private void Slider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _viewModel.CurrentVolumeChanged();
    }

    /// <summary>
    /// The active grid's full selection, ordered by view position (selection order is
    /// click order - every plural verb wants row order). Falls back to the focused
    /// item so single-select behavior is unchanged.
    /// </summary>
    private List<MediaItem> SelectedTracks()
    {
        var grid = GetActiveDataGrid();
        var selection = grid.SelectedItems?.OfType<MediaItem>().ToList() ?? [];
        if (selection.Count == 0 && _viewModel.SelectedItem != null)
        {
            selection.Add(_viewModel.SelectedItem);
        }

        return MainWindowViewModel.OrderSelectionByView(selection, _viewModel.FilteredItems);
    }

    private void ContextMenu_Play(object? sender, RoutedEventArgs e)
    {
        _viewModel.DataGridRowDoubleClick();
    }

    private void ContextMenu_PlayNext(object? sender, RoutedEventArgs e)
    {
        _viewModel.PlayNext(SelectedTracks());
    }

    private void ContextMenu_AddToQueue(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddToQueue(SelectedTracks());
    }

    private void ContextMenu_RemoveFromPlaylist(object? sender, RoutedEventArgs e)
    {
        _viewModel.RemoveTracksFromPlaylist(SelectedTracks());
    }

    private async void ContextMenu_RemoveFromLibrary(object? sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveFromLibraryAsync(SelectedTracks());
    }

    private async void ContextMenu_RemoveFromDevice(object? sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveFromDeviceAsync(SelectedTracks());
    }


    private void ContextMenu_ShowInExplorer(object? sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedItem;
        if (item?.FilePath == null || !File.Exists(item.FilePath))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", $"-R \"{item.FilePath}\"");
            }
            else
            {
                // Linux: no native "select" - open the parent directory
                var dir = Path.GetDirectoryName(item.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start("xdg-open", dir);
                }
            }
        }
        catch
        {
            // best effort - ignore platform-specific failures
        }
    }

    private void ContextMenu_Favorite(object? sender, RoutedEventArgs e)
    {
        foreach (var item in SelectedTracks())
        {
            _viewModel.ToggleFavorite(item);
        }
    }

    private async void ContextMenu_CopyUrl(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem?.StreamUrl != null && Clipboard != null)
        {
            await Clipboard.SetTextAsync(_viewModel.SelectedItem.StreamUrl);
        }
    }

    private async void ContextMenu_GetInfo(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ShowMediaInfo();
    }

    private void ContextMenu_Homepage(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem?.HomepageUrl != null)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _viewModel.SelectedItem.HomepageUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // Invalid URL
            }
        }
    }

    // -- Main-window VU meter (mirror of MiniPlayer's LCD visualizer) -----
    // Rendering and bar-layout math lives in VuMeterControl. This class only
    // drives the RAF loop and feeds audio frames in.

    private void MainLcd_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Skip clicks landing on the seek slider - the slider handles those.
        if (IsWithinSeekSlider(e.Source as Avalonia.Visual))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            _viewModel.NavigateToPlaying();
            e.Handled = true;
        }
        // Single-click LCD toggle is gone - the explicit left-chevron button
        // on the LCD now cycles between Playback / VU / Rip pages via
        // CycleLcdPageCommand. Click-to-toggle was easy to hit accidentally
        // when reaching for the seek slider.
    }

    private static bool IsWithinSeekSlider(Avalonia.Visual? source)
    {
        while (source != null)
        {
            if (source is Slider)
            {
                return true;
            }
            source = source.GetVisualParent();
        }
        return false;
    }

    private void NowPlaying_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel.NavigateToPlaying();
    }

    private async void ContextMenu_BurnToCd(object? sender, RoutedEventArgs e)
    {
        var tracks = SelectedTracks();
        if (tracks.Count == 0)
        {
            return;
        }

        await _viewModel.BurnTracksToCdAsync(tracks);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+F focuses the search box even from other controls - that's the standard
        // "jump to search" shortcut and people expect it to work no matter where focus is.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+L: go to current song (iTunes' shortcut) - jumps to the view the song
        // is playing from and centers its row. Same path as the album-art click.
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.NavigateToPlaying();
            e.Handled = true;
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        var focusedIsTextBox = focused is TextBox;

        // Enter from the search box: if nothing is currently selected in the grid, pick
        // the first filtered item and play it. If the user selected a row before typing,
        // play that. Either way, Enter acts as "play the thing you're looking at".
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (_viewModel.SelectedItem != null)
            {
                _viewModel.DataGridRowDoubleClick();
                e.Handled = true;
                return;
            }
            if (_viewModel.FilteredItems.Count > 0)
            {
                _viewModel.SelectedItem = _viewModel.FilteredItems[0];
                _viewModel.DataGridRowDoubleClick();
                e.Handled = true;
                return;
            }
        }

        // All other shortcuts are suppressed when a text field has focus - otherwise
        // typing "s" in search would toggle shuffle, etc.
        if (focusedIsTextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
            {
                _viewModel.ButtonPlayPause();
                e.Handled = true;
                break;
            }

            case Key.I when e.KeyModifiers == KeyModifiers.Control:
            {
                _ = _viewModel.ShowMediaInfo();
                e.Handled = true;
                break;
            }

            case Key.S when e.KeyModifiers == KeyModifiers.None:
            {
                _viewModel.ToggleShuffleCommand.Execute(null);
                e.Handled = true;
                break;
            }

            case Key.R when e.KeyModifiers == KeyModifiers.None:
            {
                _viewModel.CycleRepeatModeCommand.Execute(null);
                e.Handled = true;
                break;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveViewState();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    // -- Drag from MediaDataGrid (for drop into playlists, or playlist track reordering) --

    private void MediaDataGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MediaDataGrid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not MediaItem item)
        {
            return;
        }

        // Only allow dragging actual music tracks (radio stations don't belong in playlists)
        if (item.Kind != MediaKind.Music)
        {
            return;
        }

        _gridDragOrigin = e.GetPosition(MediaDataGrid);
        _gridDragItem = item;
        _gridDragRowIndex = row.Index;
        _gridPressEvent = e;

        // Read the selection NOW - see _gridPressSelection. This is the last moment it
        // still holds every row the user picked.
        _gridPressSelection = SelectedTracks();
    }

    private async void MediaDataGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gridDragOrigin == null || _gridDragItem == null || _gridPressEvent == null)
        {
            return;
        }

        if (!e.GetCurrentPoint(MediaDataGrid).Properties.IsLeftButtonPressed)
        {
            ResetGridDragState();
            return;
        }

        var current = e.GetPosition(MediaDataGrid);
        var dx = current.X - _gridDragOrigin.Value.X;
        var dy = current.Y - _gridDragOrigin.Value.Y;
        if ((dx * dx + dy * dy) < 36)
        {
            return;
        }

        DraggedMediaItem = _gridDragItem;
        _draggedPlaylistRowIndex = _gridDragRowIndex;

        // Dragging a row that's part of a multi-selection drags the WHOLE selection
        // (view-ordered); dragging an unselected row drags just that row - the
        // Explorer/iTunes convention. Resolved against the selection captured at press
        // time, not the live one, which the grid has already collapsed by now.
        DraggedMediaItems = DragPayload(_gridDragItem, _gridPressSelection);

        // Put the highlight back so the whole payload reads as selected for the duration
        // of the drag; the grid dropped it to one row on the press.
        RestoreSelection(DraggedMediaItems);

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Sidebar.MediaItemDragFormat, "media"));

        if (_viewModel.ActivePlaylistId.HasValue && _gridDragRowIndex >= 0)
        {
            data.Add(DataTransferItem.Create(PlaylistRowDragFormat, "row"));
        }
        else if (_gridDragRowIndex >= 0 && _viewModel.ActiveReorderableDevice != null
                 && _gridDragRowIndex < _viewModel.FilteredItems.Count && ReferenceEquals(_viewModel.FilteredItems[_gridDragRowIndex], DraggedMediaItem))
        {
            // Device play-order drag (Shuffle tier). Only when the grid is showing on-device order -
            // with a header sort active the display no longer maps onto the device list, which the
            // row-identity check catches (the pressed row's item must sit at that same index in
            // FilteredItems).
            data.Add(DataTransferItem.Create(DeviceRowDragFormat, "row"));
        }

        // Every drag gets a ghost now - to a playlist, to a device, out to Explorer - not
        // just row reorders. The insertion line stays reorder-only below, because it means
        // "the drop lands HERE", which is a question only a reorder asks.
        BeginDragVisuals(DraggedMediaItems);

        // Include the actual files so external apps (Telegram, Explorer, etc.) receive
        // the whole dragged selection as a file drop.
        foreach (var dragged in DraggedMediaItems)
        {
            if (dragged.FilePath != null && File.Exists(dragged.FilePath))
            {
                var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(dragged.FilePath));
                if (file != null)
                {
                    data.Add(DataTransferItem.CreateFile(file));
                }
            }
        }

        var pressEvent = _gridPressEvent;
        ResetGridDragState();

        await DragDrop.DoDragDropAsync(pressEvent, data, DragDropEffects.Move | DragDropEffects.Copy);
        DraggedMediaItem = null;
        DraggedMediaItems = [];
        _draggedPlaylistRowIndex = -1;
        HideDragVisuals();
    }

    private void MediaDataGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ResetGridDragState();
    }

    private void ResetGridDragState()
    {
        _gridDragOrigin = null;
        _gridDragItem = null;
        _gridDragRowIndex = -1;
        _gridPressEvent = null;
        _gridPressSelection = [];
    }

    /// <summary>
    /// The rows a drag carries: the whole selection when the pressed row is part of it,
    /// otherwise just the pressed row. That's the Explorer/iTunes convention - dragging a
    /// row you hadn't selected moves that row, not your selection somewhere else.
    /// </summary>
    internal static List<MediaItem> DragPayload(MediaItem? pressed, IReadOnlyList<MediaItem> selectionAtPress)
    {
        if (pressed == null)
        {
            return [];
        }

        return selectionAtPress.Contains(pressed) ? [.. selectionAtPress] : [pressed];
    }

    /// <summary>Re-applies a selection to the active grid (rows the user is dragging stay lit).</summary>
    private void RestoreSelection(IReadOnlyList<MediaItem> items)
    {
        if (items.Count < 2)
        {
            return;   // a single row is already the grid's own state - don't fight it
        }

        var grid = GetActiveDataGrid();
        if (grid.SelectedItems is null)
        {
            return;
        }

        grid.SelectedItems.Clear();
        foreach (var item in items)
        {
            grid.SelectedItems.Add(item);
        }
    }

    // ── row-reorder drag visuals: insertion line + pointer ghost + edge auto-scroll ──
    // All three ride one hit-test-invisible Canvas adorning MediaDataGrid, so nothing fights the
    // DataGridRow template. The auto-scroll runs on a timer (DragOver only fires while the pointer
    // MOVES - holding still at an edge must keep scrolling).

    private Canvas? _dragOverlay;
    private Control? _dragOverlayHost;
    private Border? _dropLine;
    private Border? _dragGhost;
    private TextBlock? _dragGhostText;
    private Avalonia.Threading.DispatcherTimer? _dragScrollTimer;
    private Point _lastDragPos;

    private void EnsureDragOverlay()
    {
        if (_dragOverlay != null)
        {
            return;
        }

        IBrush accent = this.TryFindResource("SystemControlHighlightAccentBrush", ActualThemeVariant, out var res) && res is IBrush accentRes ? accentRes : Brushes.DodgerBlue;

        _dropLine = new Border { Background = accent, Height = 2, IsVisible = false };
        _dragGhostText = new TextBlock { FontSize = 12 };
        _dragGhost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x20)),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 3),
            Opacity = 0.9,
            IsVisible = false,
            Child = _dragGhostText,
        };

        _dragOverlay = new Canvas { IsHitTestVisible = false };
        _dragOverlay.Children.Add(_dropLine);
        _dragOverlay.Children.Add(_dragGhost);

        // Adorn the WINDOW, not the grid. The ghost has to follow the pointer onto the
        // sidebar (dropping on a playlist or a device) and out past the window edge, and
        // an adorner scoped to MediaDataGrid can only paint over the grid.
        _dragOverlayHost = _dragOverlayHost ?? (Control?)this.GetVisualChildren().OfType<Control>().FirstOrDefault() ?? MediaDataGrid;
        var layer = Avalonia.Controls.Primitives.AdornerLayer.GetAdornerLayer(_dragOverlayHost);
        if (layer != null)
        {
            Avalonia.Controls.Primitives.AdornerLayer.SetAdornedElement(_dragOverlay, _dragOverlayHost);
            layer.Children.Add(_dragOverlay);
        }
    }

    /// <summary>
    /// The label riding under the cursor. One track reads as itself; several read as a
    /// count, because five titles stacked up is unreadable at pointer size and the number
    /// is the thing you actually need to confirm before letting go.
    /// </summary>
    internal static string DragGhostLabel(IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0)
        {
            return "";
        }

        if (items.Count > 1)
        {
            return $"{items.Count} tracks";
        }

        // Blank-but-present tags are common in real libraries, so whitespace counts as
        // absent throughout - otherwise a track tagged with spaces renders as a dangling
        // "Title — " or as an empty ghost.
        var item = items[0];
        var title = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title;
        var artist = string.IsNullOrWhiteSpace(item.Artist) ? null : item.Artist;
        var fileName = string.IsNullOrWhiteSpace(item.FileName) ? null : item.FileName;

        if (title != null && artist != null)
        {
            return $"{title} — {artist}";
        }

        return title ?? fileName ?? "1 track";
    }

    private void BeginDragVisuals(IReadOnlyList<MediaItem> items)
    {
        EnsureDragOverlay();
        if (_dragGhostText != null)
        {
            _dragGhostText.Text = DragGhostLabel(items);
        }
    }

    /// <summary>
    /// Moves the ghost to follow the pointer. Called for EVERY drag, from the window, so
    /// it keeps up over the sidebar as well as the grid.
    /// </summary>
    private void UpdateDragGhost(DragEventArgs e)
    {
        if (_dragGhost == null || _dragOverlayHost == null || DraggedMediaItems.Count == 0)
        {
            return;
        }

        var pos = e.GetPosition(_dragOverlayHost);
        Canvas.SetLeft(_dragGhost, pos.X + 14);
        Canvas.SetTop(_dragGhost, pos.Y + 12);
        _dragGhost.IsVisible = true;
    }

    private void UpdateDragVisuals(DragEventArgs e)
    {
        var pos = e.GetPosition(MediaDataGrid);
        _lastDragPos = pos;

        UpdateDragGhost(e);

        // The insertion line is drawn in the window-wide overlay, so grid coordinates have
        // to be translated up into it.
        var gridOrigin = _dragOverlayHost != null ? MediaDataGrid.TranslatePoint(new Point(0, 0), _dragOverlayHost) ?? new Point(0, 0) : new Point(0, 0);

        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row != null && _dropLine != null && row.TranslatePoint(new Point(0, 0), MediaDataGrid) is { } rowTop)
        {
            bool before = e.GetPosition(row).Y < row.Bounds.Height / 2;
            _dropLine.Width = MediaDataGrid.Bounds.Width;
            Canvas.SetLeft(_dropLine, gridOrigin.X);
            Canvas.SetTop(_dropLine, gridOrigin.Y + Math.Max(0, rowTop.Y + (before ? 0 : row.Bounds.Height) - 1));
            _dropLine.IsVisible = true;
        }
        else if (_dropLine != null)
        {
            _dropLine.IsVisible = false;   // past the last row - the drop still means "move to end"
        }

        if (_dragScrollTimer == null)
        {
            _dragScrollTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _dragScrollTimer.Tick += (_, _) =>
            {
                const double zone = 32, step = 26;
                var sv = MediaDataGrid.FindDescendantOfType<ScrollViewer>();
                if (sv == null)
                {
                    return;
                }
                if (_lastDragPos.Y < zone)
                {
                    sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y - step));
                }
                else if (_lastDragPos.Y > MediaDataGrid.Bounds.Height - zone)
                {
                    var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    sv.Offset = new Vector(sv.Offset.X, Math.Min(max, sv.Offset.Y + step));
                }
            };
        }
        _dragScrollTimer.Start();
    }

    /// <summary>Retires the insertion line (and its auto-scroll) but leaves the ghost alone.</summary>
    private void HideDropLine()
    {
        _dragScrollTimer?.Stop();
        if (_dropLine != null)
        {
            _dropLine.IsVisible = false;
        }
    }

    private void HideDragVisuals()
    {
        HideDropLine();
        if (_dragGhost != null)
        {
            _dragGhost.IsVisible = false;
        }
    }

    private void MediaDataGrid_DragOver(object? sender, DragEventArgs e)
    {
        // Row-reorder drags only: a playlist's rows while viewing that playlist, or a reorderable
        // device's rows (Shuffle play order) while viewing that device.
        bool playlistRow = _viewModel.ActivePlaylistId.HasValue && e.DataTransfer.Contains(PlaylistRowDragFormat);
        bool deviceRow = _viewModel.ActiveReorderableDevice != null && e.DataTransfer.Contains(DeviceRowDragFormat);
        if (!playlistRow && !deviceRow)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        UpdateDragVisuals(e);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void MediaDataGrid_Drop(object? sender, DragEventArgs e)
    {
        HideDragVisuals();

        bool playlistRow = _viewModel.ActivePlaylistId.HasValue && e.DataTransfer.Contains(PlaylistRowDragFormat);
        bool deviceRow = _viewModel.ActiveReorderableDevice != null && e.DataTransfer.Contains(DeviceRowDragFormat);
        if (!playlistRow && !deviceRow)
        {
            return;
        }

        var fromIndex = _draggedPlaylistRowIndex;
        if (fromIndex < 0)
        {
            return;
        }

        // The landing spot must match the insertion line the user saw: upper half of the target row
        // = before it, lower half = after it, past the last row = the very end.
        var targetRow = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        bool insertBefore = targetRow != null && e.GetPosition(targetRow).Y < targetRow.Bounds.Height / 2;
        int toIndex;
        if (targetRow != null)
        {
            toIndex = targetRow.Index;
        }
        else
        {
            // Dropped past last row → move to end
            toIndex = _viewModel.FilteredItems.Count - 1;
        }

        if (toIndex < 0)
        {
            toIndex = 0;
        }

        if (playlistRow)
        {
            _viewModel.ReorderPlaylistTrack(fromIndex, toIndex, insertBefore);
        }
        else
        {
            // Item-based, not index-based: the dragged item moves relative to the target row's item
            // (null target = dropped past the last row = move to the end of the device order).
            _viewModel.ReorderDeviceTrack(DraggedMediaItem, targetRow?.DataContext as MediaItem, insertBefore);
        }
        e.Handled = true;
    }
}
