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
    private readonly MainWindowViewModel _viewModel;

    /// <summary>Exposed for the docs-screenshot harness to seed view-model state.</summary>
    internal MainWindowViewModel ViewModel => _viewModel;

    private readonly Dictionary<string, EventHandler<RoutedEventArgs>> _menuHandlers;


    private string? _lastViewConfigKey;
    private readonly Dictionary<string, (double ScrollOffset, MediaItem? SelectedItem)> _viewStates = new();
    private bool _groupedDataGridInitialized;

    internal static MediaItem? DraggedMediaItem;
    private static int _draggedPlaylistRowIndex = -1;
    private static readonly DataFormat<string> PlaylistRowDragFormat = DataFormat.CreateStringApplicationFormat("OrgZ.PlaylistRowIndex");
    private PointerPressedEventArgs? _gridPressEvent;
    private Point? _gridDragOrigin;
    private MediaItem? _gridDragItem;
    private int _gridDragRowIndex = -1;

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

        MainDataGrid.AddHandler(InputElement.PointerPressedEvent, MainDataGrid_PointerPressed, RoutingStrategies.Tunnel);
        MainDataGrid.AddHandler(InputElement.PointerMovedEvent, MainDataGrid_PointerMoved, RoutingStrategies.Tunnel);
        MainDataGrid.AddHandler(InputElement.PointerReleasedEvent, MainDataGrid_PointerReleased, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(MainDataGrid, true);
        MainDataGrid.AddHandler(DragDrop.DragOverEvent, MainDataGrid_DragOver);
        MainDataGrid.AddHandler(DragDrop.DropEvent, MainDataGrid_Drop);

        // Column-header context menu: right-click any header → toggle columns + persist.
        // Wired at the tunneling stage so the DataGrid doesn't eat the event first.
        MainDataGrid.AddHandler(InputElement.PointerPressedEvent, DataGrid_HeaderRightClick, RoutingStrategies.Tunnel);
        GroupedDataGrid.AddHandler(InputElement.PointerPressedEvent, DataGrid_HeaderRightClick, RoutingStrategies.Tunnel);
        MainDataGrid.ColumnReordered += DataGrid_ColumnReordered;
        GroupedDataGrid.ColumnReordered += DataGrid_ColumnReordered;

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
            ["RestoreFromIgnored"] = ContextMenu_RestoreFromIgnored,
            ["RipTrack"] = async (s, e) => await _viewModel.RipSelectedCdTrackAsync(),
            ["RipCd"] = async (s, e) => await _viewModel.RipCurrentCdAsync(),
            ["BurnToCd"] = ContextMenu_BurnToCd,
        };

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ScrollToSelectedRequested = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.SelectedItem == null)
                {
                    return;
                }

                var grid = GetActiveDataGrid();
                grid.ScrollIntoView(_viewModel.SelectedItem, null);

                // Avalonia's DataGrid lands the target at the closest viewport edge
                // rather than centering. After ScrollIntoView realizes the row,
                // shift the scroll offset so the row sits mid-viewport.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var sv = grid.FindDescendantOfType<ScrollViewer>();
                    if (sv == null)
                    {
                        return;
                    }

                    var idx = _viewModel.FilteredItems.IndexOf(_viewModel.SelectedItem);
                    if (idx < 0)
                    {
                        return;
                    }

                    var rowHeight = grid.RowHeight > 0 ? grid.RowHeight : 22.0;
                    var rowTop = idx * rowHeight;
                    var viewportH = sv.Viewport.Height;
                    var maxOffset = Math.Max(0, sv.Extent.Height - viewportH);
                    var centered = Math.Clamp(rowTop - (viewportH - rowHeight) / 2, 0, maxOffset);
                    sv.Offset = new Vector(sv.Offset.X, centered);
                }, Avalonia.Threading.DispatcherPriority.Background);
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        _viewModel.GetScrollOffset = () => GetDataGridScrollViewer()?.Offset.Y ?? 0;
        _viewModel.SetScrollOffset = (offset) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var sv = GetDataGridScrollViewer();
                if (sv != null)
                {
                    sv.Offset = new Vector(sv.Offset.X, offset);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
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

            // Restore view state after items are loaded — use Background priority
            // so the DataGrid has time to measure and render rows first
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreViewState(_lastViewConfigKey), Avalonia.Threading.DispatcherPriority.Background);
            }, Avalonia.Threading.DispatcherPriority.Render);
        };
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedSidebarItem))
        {
            return;
        }

        // Save state of the view we're leaving
        SaveViewState();

        var sidebarItem = _viewModel.SelectedSidebarItem;
        var config = ListViewConfigs.Get(sidebarItem?.ViewConfigKey);

        if (config != null)
        {
            ApplyViewConfig(config);
        }

        // Restore state of the view we're entering (deferred until items are bound)
        _lastViewConfigKey = sidebarItem?.ViewConfigKey;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreViewState(_lastViewConfigKey), Avalonia.Threading.DispatcherPriority.Render);
    }

    private DataGrid GetActiveDataGrid()
    {
        return GroupedDataGrid.IsVisible ? GroupedDataGrid : MainDataGrid;
    }

    private ScrollViewer? GetDataGridScrollViewer()
    {
        return GetActiveDataGrid().FindDescendantOfType<ScrollViewer>();
    }

    private void SaveViewState()
    {
        if (_lastViewConfigKey == null)
        {
            return;
        }

        var sv = GetDataGridScrollViewer();
        var offset = sv?.Offset.Y ?? 0;
        var selectedId = _viewModel.SelectedItem?.Id;
        _viewStates[_lastViewConfigKey] = (offset, _viewModel.SelectedItem);

        // Persist to settings
        Settings.Set($"OrgZ.View.{_lastViewConfigKey}.Scroll", offset);
        Settings.Set($"OrgZ.View.{_lastViewConfigKey}.SelectedId", selectedId ?? string.Empty);
        Settings.Save();
    }

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

            var sv = GetDataGridScrollViewer();
            if (sv != null)
            {
                sv.Offset = new Vector(sv.Offset.X, state.ScrollOffset);
            }
            return;
        }

        // Restore from persisted settings (app restart)
        var savedScroll = Settings.Get($"OrgZ.View.{key}.Scroll", 0.0);
        var savedSelectedId = Settings.Get($"OrgZ.View.{key}.SelectedId", string.Empty);

        if (!string.IsNullOrEmpty(savedSelectedId))
        {
            var item = _viewModel.FilteredItems.FirstOrDefault(i => i.Id == savedSelectedId);
            if (item != null)
            {
                _viewModel.SelectedItem = item;
            }
        }

        if (savedScroll > 0)
        {
            var sv = GetDataGridScrollViewer();
            if (sv != null)
            {
                sv.Offset = new Vector(sv.Offset.X, savedScroll);
            }
        }
    }

    // Click handler for the error badge in the inlined view footer (the
    // chunk of XAML that used to live in the StatusBar UserControl). Opens
    // the message log dialog — same behavior as the old
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

        // Skip the assignment when nothing meaningfully changed — Width writes
        // re-trigger a layout pass, and SizeChanged fires within that pass, so
        // any oscillation here turns into a layout loop.
        if (Math.Abs(MainLcdPanel.Width - target) > 0.5)
        {
            MainLcdPanel.Width = target;
        }
    }

    private void ApplyViewConfig(ListViewConfig config)
    {
        bool isPodcasts = config.Key == "Podcasts";
        bool isGrouped = config.GroupByPath != null;

        // Podcasts uses its own UserControl instead of a DataGrid. Hide both grids
        // and show the panel; the panel's internal nav switches between store /
        // subscriptions / feed-detail without touching the DataGrid pipeline.
        PodcastsPanel.IsVisible = isPodcasts;
        MainDataGrid.IsVisible = !isPodcasts && !isGrouped;
        GroupedDataGrid.IsVisible = !isPodcasts && isGrouped;

        // Set RadioFilterPanel visibility before any early-return so a previous
        // Radio view doesn't leave its country/genre dropdowns hanging in the
        // Podcasts view.
        RadioFilterPanel.IsVisible = config.ShowRadioFilterPanel;

        if (isPodcasts)
        {
            _ = _viewModel.Podcasts.LoadStoreAsync();
            _viewModel.Podcasts.ReloadSubscriptions();
            return;
        }

        if (isGrouped)
        {
            // Build columns on GroupedDataGrid exactly once — rebuilding after a grouped
            // DataGridCollectionView was bound triggers the Avalonia spacer column bug.
            if (!_groupedDataGridInitialized)
            {
                BuildColumnsOn(GroupedDataGrid, config.Columns);
                BuildContextMenuOn(GroupedDataGrid, config.ContextMenuItems);
                _groupedDataGridInitialized = true;
            }
        }
        else
        {
            BuildColumns(config.Columns);
            BuildContextMenu(config.ContextMenuItems);
        }

        // Group expand/collapse for grouped views uses persisted per-view state. On
        // load, each group gets the state it had last time (or collapsed by default
        // for first-time keys). User toggles are captured via the header's
        // IsItemsExpanded observable and saved immediately.
        if (isGrouped)
        {
            // Key off the config being applied (the view we're entering), NOT
            // _lastViewConfigKey — that's still the view we're leaving at this point, which
            // made the radio groups load/save their collapse state under the previous view's
            // key (so it "inherited" Music's or Podcasts' state depending on where you came
            // from). config.Key is stable per grouped view.
            _currentGroupedViewKey = config.Key;
            _groupExpansion = string.IsNullOrEmpty(_currentGroupedViewKey)
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : GroupExpansionState.Load(_currentGroupedViewKey!);
            _appliedExpansionKeys.Clear();

            GroupedDataGrid.LoadingRowGroup -= AutoCollapseRowGroup;
            GroupedDataGrid.LoadingRowGroup += AutoCollapseRowGroup;

            // Apply the saved collapse state once, in a single batch, after the grid has laid
            // out the freshly-bound collection. It MUST run post-layout: CollapseRowGroup only
            // works once the DataGrid has built each group's row-group info, so applying it
            // synchronously here (before that) silently no-ops and leaves the visual expanded
            // while the stored dict says collapsed — which inverts the next tap's !prev and
            // desyncs persistence. One batch (not the per-realization path) keeps it from
            // collapsing group-by-group over time.
            Dispatcher.UIThread.Post(ApplyGroupExpansionToAllGroups, DispatcherPriority.Background);
        }
        else
        {
            _currentGroupedViewKey = null;
            _appliedExpansionKeys.Clear();
        }
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
    /// Headers we've already wired the IsItemsExpanded observer to, tracked by weak
    /// reference so re-realization of the same header gets its own subscription
    /// without leaking old ones.
    /// </summary>
    private readonly HashSet<DataGridRowGroupHeader> _observedHeaders = new();

    /// <summary>
    /// Group keys we've already applied the saved expansion state to during this view
    /// session. LoadingRowGroup fires every time a header re-enters the viewport —
    /// without this guard we'd re-apply the saved state on every scroll-in, and a
    /// programmatic Expand snaps the viewport to the expanded group (the cause of the
    /// "jumps up + opens first genre" bug when the user collapses a group further down).
    /// Reset on every view switch in <see cref="ApplyViewConfig"/>.
    /// </summary>
    private readonly HashSet<string> _appliedExpansionKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Deterministically applies the saved expand/collapse state to every group in the
    /// active grouped view, working from the collection view's group list (so it covers
    /// groups that haven't realized yet) rather than the racy per-realization path. Posted
    /// once per view entry; a no-op once the grid isn't showing a grouped view.
    /// </summary>
    private void ApplyGroupExpansionToAllGroups()
    {
        if (_currentGroupedViewKey is null
            || GroupedDataGrid.ItemsSource is not DataGridCollectionView view
            || view.Groups is null)
        {
            return;
        }

        foreach (var obj in view.Groups)
        {
            if (obj is not DataGridCollectionViewGroup group)
            {
                continue;
            }

            var key = group.Key?.ToString() ?? string.Empty;
            _appliedExpansionKeys.Add(key);

            // Default unseen groups to collapsed, recording the choice so it persists.
            var expand = _groupExpansion.TryGetValue(key, out var v) && v;
            if (!_groupExpansion.ContainsKey(key))
            {
                _groupExpansion[key] = false;
            }

            if (expand)
            {
                GroupedDataGrid.ExpandRowGroup(group, false);
            }
            else
            {
                GroupedDataGrid.CollapseRowGroup(group, false);
            }
        }

        PersistGroupExpansion();
    }

    /// <summary>
    /// Collapses every group in the active grouped view and persists it — backs the
    /// "collapse all" button on the radio filter bar. No-op outside a grouped view.
    /// </summary>
    internal void CollapseAllRowGroups()
    {
        if (GroupedDataGrid.ItemsSource is not DataGridCollectionView view || view.Groups is null)
        {
            return;
        }

        foreach (var obj in view.Groups)
        {
            if (obj is not DataGridCollectionViewGroup group)
            {
                continue;
            }
            _groupExpansion[group.Key?.ToString() ?? string.Empty] = false;
            GroupedDataGrid.CollapseRowGroup(group, false);
        }

        PersistGroupExpansion();
    }

    private void AutoCollapseRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        // No per-header collapse is applied here anymore — doing it as each header realized is
        // what made the grid collapse group-by-group "over time". The saved state is applied
        // in one batch by ApplyGroupExpansionToAllGroups (which also covers groups that
        // haven't realized yet). All that remains is wiring the tap observer once per header,
        // so user toggles keep updating the persisted dict.
        var header = e.RowGroupHeader;
        if (_observedHeaders.Add(header))
        {
            header.Tapped += OnGroupHeaderTapped;
        }
    }

    private void OnGroupHeaderTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not DataGridRowGroupHeader h || h.DataContext is not DataGridCollectionViewGroup g)
        {
            return;
        }
        var k = g.Key?.ToString() ?? string.Empty;
        var prev = _groupExpansion.TryGetValue(k, out var p) && p;
        _groupExpansion[k] = !prev;
        PersistGroupExpansion();
    }

    private void PersistGroupExpansion()
    {
        if (!string.IsNullOrEmpty(_currentGroupedViewKey))
        {
            GroupExpansionState.Save(_currentGroupedViewKey!, _groupExpansion);
        }
    }

    private void BuildColumns(List<ColumnDef> columnDefs)
    {
        BuildColumnsOn(MainDataGrid, columnDefs);
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
        grid.Columns.Clear();

        foreach (var def in columnDefs)
        {
            DataGridColumn col = def.Type switch
            {
                ColumnType.CheckBox => new DataGridCheckBoxColumn
                {
                    Header = def.Header,
                    Binding = new Binding(def.BindingPath),
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
            // even if somehow saved-off — losing either would strand the user.
            bool? savedVisible = string.IsNullOrEmpty(viewKey) ? null : ColumnStateStore.GetVisibility(viewKey!, def.Key);
            col.IsVisible = savedVisible ?? def.IsDefaultVisible;

            // Tag the column with its Key so the reorder + toggle handlers can look it
            // up by DataGridColumn without having to maintain a parallel map.
            col.SetValue(ColumnKeyProperty, def.Key);

            grid.Columns.Add(col);
        }
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
            // Skip the play-indicator / no-header columns — they're structural, can't
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
        BuildContextMenuOn(MainDataGrid, defs);
    }

    private void BuildContextMenuOn(DataGrid grid, List<ContextMenuItemDef> defs)
    {
        var menu = new ContextMenu();
        BuildMenuItems(menu.Items, defs);
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
            };

            if (def.IsHeader)
            {
                menuItem.IsHitTestVisible = false;
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
                PopulateAddToPlaylistMenu(menuItem);
            }
            else if (def.IsRatingMarker)
            {
                PopulateRatingMenu(menuItem);
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

    private void PopulateAddToPlaylistMenu(Avalonia.Controls.MenuItem parent)
    {
        var playlists = _viewModel.PlaylistItems
            .Where(p => p.PlaylistId.HasValue)
            .ToList();

        if (playlists.Count == 0)
        {
            parent.Items.Add(new Avalonia.Controls.MenuItem
            {
                Header = "(No playlists yet)",
                IsEnabled = false,
            });
            return;
        }

        foreach (var p in playlists)
        {
            var playlistId = p.PlaylistId!.Value;
            var item = new Avalonia.Controls.MenuItem { Header = p.Name };
            item.Click += (_, _) =>
            {
                if (_viewModel.SelectedItem != null)
                {
                    _viewModel.AddTrackToPlaylist(playlistId, _viewModel.SelectedItem);
                }
            };
            parent.Items.Add(item);
        }
    }

    private void RebuildContextMenu()
    {
        var config = ListViewConfigs.Get(_lastViewConfigKey);
        if (config != null)
        {
            var grid = config.GroupByPath != null ? GroupedDataGrid : MainDataGrid;
            BuildContextMenuOn(grid, config.ContextMenuItems);
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

    private void ContextMenu_Play(object? sender, RoutedEventArgs e)
    {
        _viewModel.DataGridRowDoubleClick();
    }

    private void ContextMenu_PlayNext(object? sender, RoutedEventArgs e)
    {
        _viewModel.PlayNext(_viewModel.SelectedItem);
    }

    private void ContextMenu_AddToQueue(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddToQueue(_viewModel.SelectedItem);
    }

    private void ContextMenu_RemoveFromPlaylist(object? sender, RoutedEventArgs e)
    {
        _viewModel.RemoveFromPlaylist();
    }

    private async void ContextMenu_RemoveFromLibrary(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem != null)
        {
            await _viewModel.RemoveFromLibraryAsync(_viewModel.SelectedItem);
        }
    }

    private void ContextMenu_RestoreFromIgnored(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem != null)
        {
            _viewModel.RestoreFromIgnored(_viewModel.SelectedItem);
        }
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
                // Linux: no native "select" — open the parent directory
                var dir = Path.GetDirectoryName(item.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start("xdg-open", dir);
                }
            }
        }
        catch
        {
            // best effort — ignore platform-specific failures
        }
    }

    private void ContextMenu_Favorite(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem != null)
        {
            _viewModel.ToggleFavorite(_viewModel.SelectedItem);
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
        // Skip clicks landing on the seek slider — the slider handles those.
        if (IsWithinSeekSlider(e.Source as Avalonia.Visual))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            _viewModel.NavigateToPlaying();
            e.Handled = true;
        }
        // Single-click LCD toggle is gone — the explicit left-chevron button
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
        var tracks = MainDataGrid.SelectedItems?.OfType<MediaItem>().ToList() ?? [];
        if (tracks.Count == 0 && _viewModel.SelectedItem != null)
        {
            tracks.Add(_viewModel.SelectedItem);
        }

        if (tracks.Count == 0)
        {
            return;
        }

        await _viewModel.BurnTracksToCdAsync(tracks);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+F focuses the search box even from other controls — that's the standard
        // "jump to search" shortcut and people expect it to work no matter where focus is.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
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

        // All other shortcuts are suppressed when a text field has focus — otherwise
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

    // -- Drag from MainDataGrid (for drop into playlists, or playlist track reordering) --

    private void MainDataGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MainDataGrid).Properties.IsLeftButtonPressed)
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

        _gridDragOrigin = e.GetPosition(MainDataGrid);
        _gridDragItem = item;
        _gridDragRowIndex = row.Index;
        _gridPressEvent = e;
    }

    private async void MainDataGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gridDragOrigin == null || _gridDragItem == null || _gridPressEvent == null)
        {
            return;
        }

        if (!e.GetCurrentPoint(MainDataGrid).Properties.IsLeftButtonPressed)
        {
            ResetGridDragState();
            return;
        }

        var current = e.GetPosition(MainDataGrid);
        var dx = current.X - _gridDragOrigin.Value.X;
        var dy = current.Y - _gridDragOrigin.Value.Y;
        if ((dx * dx + dy * dy) < 36)
        {
            return;
        }

        DraggedMediaItem = _gridDragItem;
        _draggedPlaylistRowIndex = _gridDragRowIndex;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Sidebar.MediaItemDragFormat, "media"));

        if (_viewModel.ActivePlaylistId.HasValue && _gridDragRowIndex >= 0)
        {
            data.Add(DataTransferItem.Create(PlaylistRowDragFormat, "row"));
        }

        // Include the actual file so external apps (Telegram, Explorer, etc.) receive it as a file drop
        if (_gridDragItem?.FilePath != null && File.Exists(_gridDragItem.FilePath))
        {
            var storage = StorageProvider;
            var file = await storage.TryGetFileFromPathAsync(new Uri(_gridDragItem.FilePath));
            if (file != null)
            {
                data.Add(DataTransferItem.CreateFile(file));
            }
        }

        var pressEvent = _gridPressEvent;
        ResetGridDragState();

        await DragDrop.DoDragDropAsync(pressEvent, data, DragDropEffects.Move | DragDropEffects.Copy);
        DraggedMediaItem = null;
        _draggedPlaylistRowIndex = -1;
    }

    private void MainDataGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ResetGridDragState();
    }

    private void ResetGridDragState()
    {
        _gridDragOrigin = null;
        _gridDragItem = null;
        _gridDragRowIndex = -1;
        _gridPressEvent = null;
    }

    private void MainDataGrid_DragOver(object? sender, DragEventArgs e)
    {
        // Only accept playlist-row drags, and only when we're currently viewing the same playlist
        if (!_viewModel.ActivePlaylistId.HasValue || !e.DataTransfer.Contains(PlaylistRowDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void MainDataGrid_Drop(object? sender, DragEventArgs e)
    {
        if (!_viewModel.ActivePlaylistId.HasValue || !e.DataTransfer.Contains(PlaylistRowDragFormat))
        {
            return;
        }

        var fromIndex = _draggedPlaylistRowIndex;
        if (fromIndex < 0)
        {
            return;
        }

        var targetRow = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
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

        _viewModel.ReorderPlaylistTrack(fromIndex, toIndex);
        e.Handled = true;
    }
}
