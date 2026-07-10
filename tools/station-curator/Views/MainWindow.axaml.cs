// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OrgZ.StationCurator.Models;
using OrgZ.StationCurator.ViewModels;

namespace OrgZ.StationCurator.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _wiredVm;

    /// <summary>Headers already wired - LoadingRowGroup re-fires every time a header re-enters the viewport.</summary>
    private readonly HashSet<DataGridRowGroupHeader> _observedHeaders = [];

    public MainWindow()
    {
        InitializeComponent();

        // DataGrid.SelectedItems is not bindable; hand the multi-selection to the VM directly.
        SourceGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SetSelectedSources(SourceGrid.SelectedItems.Cast<SourceStation>().ToList());
            }
        };

        SourceGrid.AddHandler(InputElement.DoubleTappedEvent, OnSourceDoubleTapped, RoutingStrategies.Bubble);
        CuratedGrid.AddHandler(InputElement.DoubleTappedEvent, OnCuratedDoubleTapped, RoutingStrategies.Bubble);

        CuratedGrid.LoadingRowGroup += OnLoadingRowGroup;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Genre headers are separators, not toggles. The chevron is hidden in XAML; this is the
    /// safety net for the remaining collapse paths (keyboard arrows on a focused header) -
    /// any header whose expander goes unchecked gets its group re-expanded immediately.
    /// </summary>
    private void OnLoadingRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        var header = e.RowGroupHeader;

        // Empty genres are represented by one hidden placeholder row, so their headers
        // would claim "(1 Item)" - suppress the count for those groups. Evaluated on every
        // fire AND on DataContext swaps: recycled headers move between groups.
        UpdateItemCountVisibility(header);

        if (!_observedHeaders.Add(header))
        {
            return;
        }

        header.DataContextChanged += (_, _) => UpdateItemCountVisibility(header);

        if (header.FindDescendantOfType<ToggleButton>() is { } expander)
        {
            WireExpander(header, expander);
        }
        else
        {
            // Freshly created headers haven't built their template yet when LoadingRowGroup fires.
            header.TemplateApplied += (_, args) =>
            {
                if (args.NameScope.Find<ToggleButton>("PART_ExpanderButton") is { } late)
                {
                    WireExpander(header, late);
                }
            };
        }
    }

    /// <summary>"(1 Item)" on a group whose only row is the hidden empty-genre placeholder is a lie - hide the count there, show it everywhere else.</summary>
    private static void UpdateItemCountVisibility(DataGridRowGroupHeader header)
    {
        header.IsItemCountVisible = header.DataContext is not DataGridCollectionViewGroup group
            || group.ItemCount != 1
            || (group.Items[0] as CuratedStation)?.IsPlaceholder != true;
    }

    private void WireExpander(DataGridRowGroupHeader header, ToggleButton expander)
    {
        expander.IsCheckedChanged += (_, _) =>
        {
            if (expander.IsChecked == false && header.DataContext is DataGridCollectionViewGroup group)
            {
                Dispatcher.UIThread.Post(() => CuratedGrid.ExpandRowGroup(group, false), DispatcherPriority.Background);
            }
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wiredVm != null)
        {
            _wiredVm.CuratedRevealRequested -= RevealCurated;
        }
        _wiredVm = DataContext as MainViewModel;
        if (_wiredVm != null)
        {
            _wiredVm.CuratedRevealRequested += RevealCurated;
        }
    }

    /// <summary>
    /// "Show in Curated": bring the row into view. Posted post-layout - the reveal follows a
    /// CuratedView rebuild, and ScrollIntoView needs the grid to have materialized its slots.
    /// </summary>
    private void RevealCurated(CuratedStation station)
    {
        Dispatcher.UIThread.Post(() => CuratedGrid.ScrollIntoView(station, null), DispatcherPriority.Background);
    }

    /// <summary>
    /// Empty-genre placeholders exist only to force their group header into existence -
    /// the DataGridCollectionView grouping model can't represent an itemless group. Hide
    /// the row itself so an empty genre renders as a bare header. Rows are recycled, so
    /// visibility is set BOTH ways on every load.
    /// </summary>
    private void OnCuratedRowLoading(object? sender, DataGridRowEventArgs e)
    {
        e.Row.IsVisible = (e.Row.DataContext as CuratedStation)?.IsPlaceholder != true;
    }

    private void OnSourceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.PreviewSourceCommand.Execute(null);
        }
    }

    private void OnCuratedDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.PlayBestOfSelectedCurated();
        }
    }
}
