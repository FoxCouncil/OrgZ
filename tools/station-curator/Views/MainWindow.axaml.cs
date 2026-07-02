// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrgZ.StationCurator.Models;
using OrgZ.StationCurator.ViewModels;

namespace OrgZ.StationCurator.Views;

public partial class MainWindow : Window
{
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
