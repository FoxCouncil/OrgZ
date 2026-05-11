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
using OrgZ.ViewModels;
using Optris.Icons.Avalonia;

namespace OrgZ.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    private readonly Dictionary<string, EventHandler<RoutedEventArgs>> _menuHandlers;

    private CancellationTokenSource? _liveBarAnimationCts;
    private CancellationTokenSource? _marquee1Cts;
    private CancellationTokenSource? _marquee2Cts;

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

    // iTunes-style stereo segmented VU: columns × rows of ticks per channel.
    // Left channel mirrored so lows meet at the center, highs flare outward.
    // Tick size derived from canvas dimensions so the meter scales with the
    // LCD rather than sitting tiny in the middle.
    private const int MainVuColumnsPerChannel = 16;
    private const int MainVuRowsPerColumn = 16;
    private const double MainVuChannelGap = 16;
    private const double MainVuTickGap = 1;
    private double _mainVuTickWidth = 6;
    private double _mainVuTickHeight = 2;

    private readonly Avalonia.Controls.Shapes.Rectangle[,] _mainVuTicksLeft = new Avalonia.Controls.Shapes.Rectangle[MainVuColumnsPerChannel, MainVuRowsPerColumn];
    private readonly Avalonia.Controls.Shapes.Rectangle[,] _mainVuTicksRight = new Avalonia.Controls.Shapes.Rectangle[MainVuColumnsPerChannel, MainVuRowsPerColumn];
    private readonly float[] _mainVuLevelsLeft = new float[MainVuColumnsPerChannel];
    private readonly float[] _mainVuLevelsRight = new float[MainVuColumnsPerChannel];
    private readonly int[] _mainVuLastLitLeft = new int[MainVuColumnsPerChannel];
    private readonly int[] _mainVuLastLitRight = new int[MainVuColumnsPerChannel];
    private readonly float[] _mainVuPeakLeft = new float[MainVuColumnsPerChannel];
    private readonly float[] _mainVuPeakRight = new float[MainVuColumnsPerChannel];
    private readonly Avalonia.Controls.Shapes.Rectangle[] _mainVuPeakMarkLeft = new Avalonia.Controls.Shapes.Rectangle[MainVuColumnsPerChannel];
    private readonly Avalonia.Controls.Shapes.Rectangle[] _mainVuPeakMarkRight = new Avalonia.Controls.Shapes.Rectangle[MainVuColumnsPerChannel];

    private const float MainVuDecayStep = 0.05f;
    private const float MainVuPeakDecayStep = 0.012f;
    private Avalonia.Threading.DispatcherTimer? _mainVuTimer;
    private bool _mainVuMode;

    private static readonly Avalonia.Media.IBrush MainVuOnBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A4A30"));
    private static readonly Avalonia.Media.IBrush MainVuOffBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(80, 0x3A, 0x4A, 0x30));
    private static readonly Avalonia.Media.IBrush MainVuPeakBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E2014"));

    public MainWindow()
    {
        InitializeComponent();

        WindowSizeTracker.Track(this, "Main");

        BuildMainVuBars();

        var slider = this.FindControl<Slider>("CurrentTimeSlider")!;

        slider.AddHandler(InputElement.PointerPressedEvent, Slider_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, Slider_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

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

        DataContext = _viewModel = new MainWindowViewModel(this);

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
                if (_viewModel.SelectedItem != null)
                {
                    MainDataGrid.ScrollIntoView(_viewModel.SelectedItem, null);
                }
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

        var radioFilterPanel = this.FindControl<Controls.RadioFilterPanel>("RadioFilterPanel")!;
        radioFilterPanel.SyncRequested += () => _viewModel.LaunchRadioSync();

        var breadcrumb = this.FindControl<Controls.BreadcrumbBar>("BreadcrumbBar")!;
        breadcrumb.RootClicked += () => _viewModel.DrillUpToRoot();
        breadcrumb.ArtistClicked += () => _viewModel.DrillUpToArtist();

        var statusBar = this.FindControl<Controls.StatusBar>("MainStatusBar")!;
        statusBar.ErrorButtonClicked += async () => await _viewModel.ShowMessageLog();

        Loaded += async (s, e) =>
        {
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

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSeekEnabled))
        {
            UpdateLiveBarAnimation();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentTrackLine1) || e.PropertyName == nameof(MainWindowViewModel.CurrentTrackLine2))
        {
            RestartMarquees();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsDrillDownActive) || e.PropertyName == nameof(MainWindowViewModel.DrillDownEntries))
        {
            UpdateDrillDownView();
            return;
        }

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

        // First-run only: if DB had no radio stations, fetch popular ones
        var kind = sidebarItem?.Kind;
        if (kind == MediaKind.Radio && _viewModel.FilteredItems.Count == 0 && !_viewModel.IsLoading)
        {
            await _viewModel.FetchPopularStationsAsync();
        }
    }

    private ScrollViewer? GetDataGridScrollViewer()
    {
        return MainDataGrid.FindDescendantOfType<ScrollViewer>();
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

    private void UpdateLiveBarAnimation()
    {
        _liveBarAnimationCts?.Cancel();
        _liveBarAnimationCts = null;

        var indicator = this.FindControl<Border>("LiveStreamIndicator");
        if (indicator == null)
        {
            return;
        }

        if (_viewModel.IsSeekEnabled)
        {
            indicator.RenderTransform = null;
            return;
        }

        // Animate: slide the glow bar back and forth across the track (420px wide, indicator is 120px)
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(2),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(TranslateTransform.XProperty, 0.0) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(TranslateTransform.XProperty, 300.0) }
                },
            }
        };

        indicator.RenderTransform = new TranslateTransform();
        _liveBarAnimationCts = new CancellationTokenSource();
        animation.RunAsync(indicator, _liveBarAnimationCts.Token);
    }

    private void RestartMarquees()
    {
        _marquee1Cts?.Cancel();
        _marquee2Cts?.Cancel();

        var tb1 = this.FindControl<TextBlock>("TrackLine1");
        var tb2 = this.FindControl<TextBlock>("TrackLine2");
        const double containerWidth = 420;

        ResetMarqueeTextBlock(tb1);
        ResetMarqueeTextBlock(tb2);

        var cts = new CancellationTokenSource();
        _marquee1Cts = cts;
        _marquee2Cts = cts;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var overflow1 = MeasureOverflow(tb1, containerWidth);
            var overflow2 = MeasureOverflow(tb2, containerWidth);

            // Center or extend each line
            SetupMarqueeTextBlock(tb1, overflow1, containerWidth);
            SetupMarqueeTextBlock(tb2, overflow2, containerWidth);

            if (overflow1 <= 0 && overflow2 <= 0)
            {
                return;
            }

            // Both lines scroll at the same speed (40px/s).
            // Shorter one waits at each end for the longer one to finish.
            // Full cycle: 5s dwell -> scroll forward -> 5s dwell -> scroll back
            var maxOverflow = Math.Max(overflow1, overflow2);
            var maxScrollSec = maxOverflow / 40.0;
            var dwellSec = 5.0;
            var totalSec = 2 * dwellSec + 2 * maxScrollSec;

            if (overflow1 > 0 && tb1 != null)
            {
                RunSyncedMarquee(tb1, overflow1, totalSec, dwellSec, maxScrollSec, cts);
            }

            if (overflow2 > 0 && tb2 != null)
            {
                RunSyncedMarquee(tb2, overflow2, totalSec, dwellSec, maxScrollSec, cts);
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private static void ResetMarqueeTextBlock(TextBlock? tb)
    {
        if (tb == null)
        {
            return;
        }

        tb.RenderTransform = null;
        tb.Width = double.NaN;
    }

    private static double MeasureOverflow(TextBlock? tb, double containerWidth)
    {
        if (tb == null || string.IsNullOrEmpty(tb.Text))
        {
            return 0;
        }

        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(0, tb.DesiredSize.Width - containerWidth);
    }

    private static void SetupMarqueeTextBlock(TextBlock? tb, double overflow, double containerWidth)
    {
        if (tb == null || string.IsNullOrEmpty(tb.Text))
        {
            return;
        }

        if (overflow <= 0)
        {
            tb.RenderTransform = new TranslateTransform((containerWidth - tb.DesiredSize.Width) / 2, 0);
        }
        else
        {
            tb.Width = tb.DesiredSize.Width;
            tb.RenderTransform = new TranslateTransform();
        }
    }

    private static void RunSyncedMarquee(TextBlock tb, double overflow, double totalSec, double dwellSec, double maxScrollSec, CancellationTokenSource cts)
    {
        // This line scrolls at 40px/s for its own distance, then waits for the longer line.
        // Cycle: 5s dwell | scroll fwd | wait | 5s dwell | scroll back | wait
        var lineScrollSec = overflow / 40.0;

        double CueFrac(double seconds) => Math.Min(seconds / totalSec, 1.0);

        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(totalSec),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                // Dwell at start
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec)), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                // Scroll forward (arrives early if shorter, holds at -overflow)
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + lineScrollSec)), Setters = { new Setter(TranslateTransform.XProperty, -overflow) } },
                // Dwell at end (starts when the longer line finishes forward scroll)
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + maxScrollSec + dwellSec)), Setters = { new Setter(TranslateTransform.XProperty, -overflow) } },
                // Scroll back (arrives early if shorter, holds at 0)
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + maxScrollSec + dwellSec + lineScrollSec)), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                // Wait for longer line to finish scrolling back
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        animation.RunAsync(tb, cts.Token);
    }

    private void ApplyViewConfig(ListViewConfig config)
    {
        bool isGrouped = config.GroupByPath != null;

        // Toggle between two DataGrids — grouped views use GroupedDataGrid (columns set once),
        // non-grouped views use MainDataGrid (columns rebuilt per view). This avoids an Avalonia
        // bug where the DataGrid's internal RowGroupSpacerColumn state corrupts column insertion
        // after a grouped view was bound.
        MainDataGrid.IsVisible = !isGrouped;
        GroupedDataGrid.IsVisible = isGrouped;

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

        RadioFilterPanel.IsVisible = config.ShowRadioFilterPanel;

        // Group expand/collapse for grouped views uses persisted per-view state. On
        // load, each group gets the state it had last time (or collapsed by default
        // for first-time keys). User toggles are captured via the header's
        // IsItemsExpanded observable and saved immediately.
        if (isGrouped)
        {
            _currentGroupedViewKey = _lastViewConfigKey ?? _viewModel.SelectedSidebarItem?.ViewConfigKey;
            _groupExpansion = string.IsNullOrEmpty(_currentGroupedViewKey)
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : GroupExpansionState.Load(_currentGroupedViewKey!);

            GroupedDataGrid.LoadingRowGroup -= AutoCollapseRowGroup;
            GroupedDataGrid.LoadingRowGroup += AutoCollapseRowGroup;
        }
        else
        {
            _currentGroupedViewKey = null;
        }

        // Reset drill-down when switching views
        if (_viewModel.DrillDownState != null)
        {
            _viewModel.DrillUpToRoot();
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

    private void AutoCollapseRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        var header = e.RowGroupHeader;
        if (header.DataContext is not DataGridCollectionViewGroup group)
        {
            return;
        }

        var keyString = group.Key?.ToString() ?? string.Empty;

        // Decide target state: saved value if we've seen the key before, else default
        // to collapsed (and record it so next time we load, we don't keep re-collapsing).
        bool shouldBeExpanded;
        if (_groupExpansion.TryGetValue(keyString, out var saved))
        {
            shouldBeExpanded = saved;
        }
        else
        {
            shouldBeExpanded = false;
            _groupExpansion[keyString] = false;
            PersistGroupExpansion();
        }

        // Apply the target state. The toggle happens on a dispatcher post because doing
        // it synchronously inside LoadingRowGroup re-entrantly can corrupt the grid's
        // realization state — the same reason the original auto-collapse used a post.
        Dispatcher.UIThread.Post(() =>
        {
            if (shouldBeExpanded)
            {
                GroupedDataGrid.ExpandRowGroup(group, true);
            }
            else
            {
                GroupedDataGrid.CollapseRowGroup(group, true);
            }
        });

        // Avalonia's DataGridRowGroupHeader doesn't publicly expose its expansion state
        // as a styled property, so we can't observe user toggles via PropertyChanged.
        // Instead: our dict IS the source of truth. Every Tapped on the header is a
        // toggle — rows inside the group don't bubble up to the header's Tapped since
        // they're siblings in the grid's visual tree, not children. We flip our dict
        // on each tap and trust that Avalonia flips the visual to match.
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

    private void UpdateDrillDownView()
    {
        var state = _viewModel.DrillDownState;
        var breadcrumb = this.FindControl<Controls.BreadcrumbBar>("BreadcrumbBar");
        var drillGrid = this.FindControl<DataGrid>("DrillDownGrid");

        if (state == null || drillGrid == null)
        {
            // Not in drill-down mode — show main grid, hide drill grid
            if (drillGrid != null)
            {
                drillGrid.IsVisible = false;
            }

            MainDataGrid.IsVisible = true;
            return;
        }

        breadcrumb?.Update(state);

        if (state.Level == DrillDownLevel.Songs)
        {
            // Songs level uses the main DataGrid with FilteredItems
            drillGrid.IsVisible = false;
            MainDataGrid.IsVisible = true;
            return;
        }

        // Artists or Albums level — use the drill-down grid
        MainDataGrid.IsVisible = false;
        drillGrid.IsVisible = true;

        drillGrid.Columns.Clear();
        var columns = state.Level == DrillDownLevel.Artists ? ListViewConfigs.BuildArtistsColumns() : ListViewConfigs.BuildAlbumsColumns();
        BuildColumnsOn(drillGrid, columns);
    }

    private void DrillDownGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var drillGrid = this.FindControl<DataGrid>("DrillDownGrid");
        if (drillGrid?.SelectedItem is DrillDownEntry entry)
        {
            _viewModel.DrillInto(entry);
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
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var icon = new Icon
                        {
                            Value = "fa-solid fa-play",
                            FontSize = 14,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new SolidColorBrush(Color.Parse("#0096FF")),
                        };
                        icon.Bind(IsVisibleProperty, new Binding("IsPlaying"));
                        return icon;
                    }),
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

    private void BuildMainVuBars()
    {
        MainVuCanvas.Children.Clear();
        AllocateMainTicks(_mainVuTicksLeft);
        AllocateMainTicks(_mainVuTicksRight);
        AllocateMainPeakMarks(_mainVuPeakMarkLeft);
        AllocateMainPeakMarks(_mainVuPeakMarkRight);

        foreach (var t in _mainVuTicksLeft) MainVuCanvas.Children.Add(t);
        foreach (var t in _mainVuTicksRight) MainVuCanvas.Children.Add(t);
        foreach (var p in _mainVuPeakMarkLeft) MainVuCanvas.Children.Add(p);
        foreach (var p in _mainVuPeakMarkRight) MainVuCanvas.Children.Add(p);

        MainVuCanvas.SizeChanged += (_, _) => LayoutMainVuBars();
    }

    private static void AllocateMainPeakMarks(Avalonia.Controls.Shapes.Rectangle[] marks)
    {
        for (int c = 0; c < marks.Length; c++)
        {
            var r = new Avalonia.Controls.Shapes.Rectangle { Fill = MainVuPeakBrush, Opacity = 0 };
            Avalonia.Media.RenderOptions.SetEdgeMode(r, Avalonia.Media.EdgeMode.Aliased);
            marks[c] = r;
        }
    }

    private static void AllocateMainTicks(Avalonia.Controls.Shapes.Rectangle[,] grid)
    {
        for (int c = 0; c < grid.GetLength(0); c++)
        {
            for (int r = 0; r < grid.GetLength(1); r++)
            {
                var rect = new Avalonia.Controls.Shapes.Rectangle { Fill = MainVuOffBrush };
                Avalonia.Media.RenderOptions.SetEdgeMode(rect, Avalonia.Media.EdgeMode.Aliased);
                grid[c, r] = rect;
            }
        }
    }

    private void LayoutMainVuBars()
    {
        var w = MainVuCanvas.Bounds.Width;
        var h = MainVuCanvas.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // All math in physical pixels so positions land on device-pixel
        // boundaries.  Avalonia renders fractional DIP positions with AA
        // that varies per column/row depending on their exact fraction —
        // that's where the drifting row-spacing came from.
        var scale = RenderScaling;

        int canvasW_phys = (int)Math.Floor(w * scale);
        int canvasH_phys = (int)Math.Floor(h * scale);
        int channelGap_phys = (int)Math.Round(MainVuChannelGap * scale);
        int tickGap_phys = Math.Max(1, (int)Math.Round(MainVuTickGap * scale));

        int availableW_phys = (canvasW_phys - channelGap_phys) / 2;
        int tickW_phys = Math.Max(2, (availableW_phys - (MainVuColumnsPerChannel - 1) * tickGap_phys) / MainVuColumnsPerChannel);
        int tickH_phys = Math.Max(2, (canvasH_phys - (MainVuRowsPerColumn - 1) * tickGap_phys) / MainVuRowsPerColumn);

        _mainVuTickWidth = tickW_phys / scale;
        _mainVuTickHeight = tickH_phys / scale;

        int channelW_phys = MainVuColumnsPerChannel * tickW_phys + (MainVuColumnsPerChannel - 1) * tickGap_phys;
        int totalW_phys = channelW_phys * 2 + channelGap_phys;
        int startX_phys = Math.Max(0, (canvasW_phys - totalW_phys) / 2);

        int totalH_phys = MainVuRowsPerColumn * tickH_phys + (MainVuRowsPerColumn - 1) * tickGap_phys;
        int bottomPad_phys = Math.Max(0, (canvasH_phys - totalH_phys) / 2);

        PositionMainChannel(_mainVuTicksLeft, startX_phys, tickW_phys, tickH_phys, tickGap_phys, bottomPad_phys, scale, mirror: false);
        PositionMainChannel(_mainVuTicksRight, startX_phys + channelW_phys + channelGap_phys, tickW_phys, tickH_phys, tickGap_phys, bottomPad_phys, scale, mirror: true);

        PositionMainPeakMarks(_mainVuPeakMarkLeft, startX_phys, tickW_phys, tickH_phys, tickGap_phys, scale, mirror: false);
        PositionMainPeakMarks(_mainVuPeakMarkRight, startX_phys + channelW_phys + channelGap_phys, tickW_phys, tickH_phys, tickGap_phys, scale, mirror: true);
    }

    private void PositionMainPeakMarks(Avalonia.Controls.Shapes.Rectangle[] marks, int originX_phys, int tickW_phys, int tickH_phys, int tickGap_phys, double scale, bool mirror)
    {
        double tickW = tickW_phys / scale;
        double tickH = tickH_phys / scale;
        for (int c = 0; c < MainVuColumnsPerChannel; c++)
        {
            int visualIndex = mirror ? (MainVuColumnsPerChannel - 1 - c) : c;
            int colX_phys = originX_phys + visualIndex * (tickW_phys + tickGap_phys);
            marks[c].Width = tickW;
            marks[c].Height = tickH;
            Canvas.SetLeft(marks[c], colX_phys / scale);
        }
    }

    private void PositionMainChannel(Avalonia.Controls.Shapes.Rectangle[,] grid, int originX_phys, int tickW_phys, int tickH_phys, int tickGap_phys, int bottomPad_phys, double scale, bool mirror)
    {
        double tickW = tickW_phys / scale;
        double tickH = tickH_phys / scale;

        for (int c = 0; c < MainVuColumnsPerChannel; c++)
        {
            int visualIndex = mirror ? (MainVuColumnsPerChannel - 1 - c) : c;
            int colX_phys = originX_phys + visualIndex * (tickW_phys + tickGap_phys);
            double colX = colX_phys / scale;

            for (int r = 0; r < MainVuRowsPerColumn; r++)
            {
                int rowY_phys = bottomPad_phys + r * (tickH_phys + tickGap_phys);
                double rowY = rowY_phys / scale;

                var rect = grid[c, r];
                rect.Width = tickW;
                rect.Height = tickH;
                Canvas.SetLeft(rect, colX);
                Canvas.SetBottom(rect, rowY);
            }
        }
    }

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
            return;
        }

        ToggleMainVuMode();
        e.Handled = true;
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

    private void ToggleMainVuMode()
    {
        _mainVuMode = !_mainVuMode;

        // Opacity-swap so both layers stay in layout (LCD keeps its shape).
        MainLcdTextLayer.Opacity = _mainVuMode ? 0 : 1;
        MainLcdTextLayer.IsHitTestVisible = !_mainVuMode;
        MainVuCanvas.Opacity = _mainVuMode ? 1 : 0;
        MainVuCanvas.IsHitTestVisible = _mainVuMode;

        if (_mainVuMode)
        {
            LayoutMainVuBars();
            _mainVuTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(40), Avalonia.Threading.DispatcherPriority.Normal, (_, _) => TickMainVuMeter());
            _mainVuTimer.Start();
        }
        else
        {
            _mainVuTimer?.Stop();
            // Reset every tick to "off" and clear the last-lit cache so
            // the next toggle-on starts from a known state.
            foreach (var t in _mainVuTicksLeft) t.Fill = MainVuOffBrush;
            foreach (var t in _mainVuTicksRight) t.Fill = MainVuOffBrush;
            Array.Clear(_mainVuLastLitLeft);
            Array.Clear(_mainVuLastLitRight);
        }
    }

    private void TickMainVuMeter()
    {
        var source = (DataContext as OrgZ.ViewModels.MainWindowViewModel)?.AudioVisualization;
        if (source == null)
        {
            return;
        }

        Span<float> left = stackalloc float[source.BandCount];
        Span<float> right = stackalloc float[source.BandCount];
        source.CopyBandLevelsStereo(left, right);

        FoldAndRenderMain(left, _mainVuTicksLeft, _mainVuLevelsLeft, _mainVuLastLitLeft, _mainVuPeakLeft, _mainVuPeakMarkLeft);
        FoldAndRenderMain(right, _mainVuTicksRight, _mainVuLevelsRight, _mainVuLastLitRight, _mainVuPeakRight, _mainVuPeakMarkRight);
    }

    private void FoldAndRenderMain(Span<float> source, Avalonia.Controls.Shapes.Rectangle[,] ticks, float[] smoothed, int[] lastLit, float[] peaks, Avalonia.Controls.Shapes.Rectangle[] peakMarks)
    {
        var srcLen = source.Length;
        if (srcLen == 0)
        {
            return;
        }

        for (int c = 0; c < MainVuColumnsPerChannel; c++)
        {
            int start = c * srcLen / MainVuColumnsPerChannel;
            int end = (c + 1) * srcLen / MainVuColumnsPerChannel;
            if (end <= start) end = start + 1;

            float sum = 0;
            for (int i = start; i < end; i++)
            {
                sum += source[i];
            }
            float target = Math.Clamp(sum / (end - start), 0f, 1f);

            // UI-side smoothing: fast attack, slow linear decay toward target.
            if (target > smoothed[c])
            {
                smoothed[c] = target;
            }
            else
            {
                smoothed[c] = Math.Max(target, smoothed[c] - MainVuDecayStep);
            }

            if (smoothed[c] > peaks[c])
            {
                peaks[c] = smoothed[c];
            }
            else
            {
                peaks[c] = Math.Max(smoothed[c], peaks[c] - MainVuPeakDecayStep);
            }

            int lit = (int)Math.Round(smoothed[c] * MainVuRowsPerColumn);
            int previousLit = lastLit[c];

            if (lit != previousLit)
            {
                int low = Math.Min(previousLit, lit);
                int high = Math.Max(previousLit, lit);
                for (int r = low; r < high; r++)
                {
                    ticks[c, r].Fill = r < lit ? MainVuOnBrush : MainVuOffBrush;
                }
                lastLit[c] = lit;
            }

            int peakRow = Math.Min(MainVuRowsPerColumn - 1, (int)Math.Round(peaks[c] * MainVuRowsPerColumn));
            var mark = peakMarks[c];
            if (peakRow > lit && peaks[c] > 0.02f)
            {
                Canvas.SetBottom(mark, Canvas.GetBottom(ticks[c, peakRow]));
                mark.Opacity = 1;
            }
            else
            {
                mark.Opacity = 0;
            }
        }
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

    private void Slider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel.CurrentTrackTimeNumberPointerPressed();
    }

    private void Slider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _viewModel.CurrentTrackTimeNumberPointerReleased();
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
