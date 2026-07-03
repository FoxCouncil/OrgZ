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
        if (!_observedHeaders.Add(header))
        {
            return;
        }

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
